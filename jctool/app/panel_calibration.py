"""The Calibration screen: guided Sticks and Motion calibration, and the original editor
(Manual), each in its own tab."""
import math

from kivy.clock import Clock
from kivy.graphics import Color, Ellipse, Line, Rectangle
from kivy.metrics import dp
from kivy.uix.boxlayout import BoxLayout
from kivy.uix.gridlayout import GridLayout
from kivy.uix.screenmanager import NoTransition, Screen, ScreenManager
from kivy.uix.widget import Widget

from .. import calibration
from ..calibration import MotionWizard, StickCal, StickWizard
from ..hidio import JOYCON_L, JOYCON_R, PROCON
from .panels import Panel
from .widgets import (ACCENT, BACK, DARK, DIM, ERROR, GRID, TEXT, WARN, Btn, Check, Field, Filled, Lbl,
                      Section, Tab, confirm, message)


def mark(done, current):
    return "✔ " if done else "▶ " if current else "    "


class StickView(Widget):
    """The stick's position, the direction ring (lit where reached), the trace and range."""

    def __init__(self, wizard, **kw):
        super().__init__(**kw)
        self.wizard = wizard
        self.bind(pos=self.draw, size=self.draw)

    def draw(self, *_):
        w = self.wizard
        self.canvas.clear()
        size = min(self.width, self.height)
        cx, cy = self.center
        ring = size / 2 - dp(16)
        with self.canvas:
            Color(*DARK)
            Rectangle(pos=self.pos, size=self.size)
            Color(*GRID)
            Line(circle=(cx, cy, ring))
            Line(points=[cx - ring, cy, cx + ring, cy])
            Line(points=[cx, cy - ring, cx, cy + ring])
            rotating = w.step >= w.ROTATE
            ox, oy = w.center if rotating else (2048, 2048)
            scale = ring / 1500.0
            m = lambda x, y: (cx + (x - ox) * scale, cy + (y - oy) * scale)
            if rotating:
                n = w.SECTORS
                for i in range(n):
                    lit = w.sectors[i] or w.step == w.DONE
                    Color(*(ACCENT if lit else GRID))
                    # Sector i covers atan2 angles from i/n*360-180; Kivy's arc angles are clockwise from 12 o'clock
                    a0 = i * 360.0 / n - 180.0
                    start = 90.0 - (a0 + 360.0 / n) + 1.5
                    Line(circle=(cx, cy, ring + dp(9), start, start + 360.0 / n - 3.0), width=dp(3))
                Color(ACCENT[0], ACCENT[1], ACCENT[2], 0.6)
                pts = []
                for x, y in w.trace[-1500:]:
                    pts.extend(m(x, y))
                if len(pts) >= 4:
                    Line(points=pts, width=1)
                Color(*WARN)
                x0, y0 = m(w.min_x, w.min_y)
                x1, y1 = m(w.max_x, w.max_y)
                Line(rectangle=(x0, y0, x1 - x0, y1 - y0), dash_length=dp(4), dash_offset=dp(3))
            if w.step == w.CENTER and len(w.recent) > 1:
                r = dp(30) * (1 - w.hold_progress) + dp(6)
                Color(*ACCENT)
                Line(circle=(cx, cy, r), width=dp(1.5))
            if w.x >= 0:
                Color(1, 1, 1, 1)
                px, py = m(w.x, w.y)
                Ellipse(pos=(px - dp(6), py - dp(6)), size=(dp(12), dp(12)))


class SticksTab(BoxLayout):
    def __init__(self, panel):
        super().__init__(orientation="horizontal", spacing=dp(10))
        self.panel = panel
        self.left = True
        self.wizard = StickWizard(True)
        self.measuring = False
        left = BoxLayout(orientation="vertical", spacing=dp(6), size_hint_x=0.5)
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        self.btn_l = Tab("Left stick", "stick")
        self.btn_r = Tab("Right stick", "stick")
        self.btn_l.bind(on_press=lambda *_: self.select(True))
        self.btn_r.bind(on_press=lambda *_: self.select(False))
        row.add_widget(self.btn_l)
        row.add_widget(self.btn_r)
        left.add_widget(row)
        self.view = StickView(self.wizard)
        left.add_widget(self.view)
        self.add_widget(left)
        right = BoxLayout(orientation="vertical", spacing=dp(6), size_hint_x=0.5)
        self.steps = Lbl("", valign="top", size_hint_y=0.45)
        right.add_widget(self.steps)
        self.values = Lbl("", valign="top", color=TEXT, font_size=dp(13), size_hint_y=0.3)
        right.add_widget(self.values)
        self.info = Lbl("", valign="top", size_hint_y=0.25)
        right.add_widget(self.info)
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        self.btn_start = Btn("Start", self.start)
        self.btn_finish = Btn("Finish", self.finish)
        self.btn_factory = Btn("Use factory", self.use_factory, color=WARN)
        self.btn_save = Btn("Save", self.save)
        for b in (self.btn_start, self.btn_finish, self.btn_factory, self.btn_save):
            row.add_widget(b)
        right.add_widget(row)
        self.add_widget(right)
        self.event = None
        self.refresh()

    @property
    def jc(self):
        return self.panel.jc

    def on_device(self):
        t = self.jc.type
        pro = t == PROCON
        self.btn_l.disabled = self.btn_r.disabled = not pro
        self.btn_l.opacity = self.btn_r.opacity = 1 if pro else 0     # A Joy-Con has one stick
        self.select(t != JOYCON_R)

    def select(self, left):
        self.stop()
        self.left = left
        self.btn_l.state = "down" if left else "normal"
        self.btn_r.state = "normal" if left else "down"
        self.wizard.left = left
        self.wizard.reset()
        self.refresh()

    def stick_name(self):
        if self.jc is not None and self.jc.type == PROCON:
            return "left stick" if self.left else "right stick"
        return "stick"

    def start(self):
        self.stop()
        self.wizard.left = self.left
        self.wizard.start()
        self.measuring = True

        def work(jc):
            jc.set_input_report_mode(0x30)
            while self.measuring:
                n, r = jc.read(49, 20)
                if n > 12 and r[0] == 0x30:
                    self.wizard.add_report(r)
                jc.ui.poll()
            jc.set_input_report_mode(0x3F)
        if self.panel.run(work, lambda r: self.refresh(), long_running=True, name="the stick calibration"):
            self.event = Clock.schedule_interval(lambda dt: self.refresh(), 0.05)

    def stop(self):
        self.measuring = False
        if self.event is not None:
            self.event.cancel()
            self.event = None

    def finish(self):
        if self.wizard.finish():
            self.stop()
        self.refresh()

    def save(self):
        cal = self.wizard.result
        if cal is None:
            return
        left = self.left

        def done(r):
            self.info.text = "Saved. The controller now uses this calibration." if r == 0 else "Failed to write the calibration."
            self.info.color = ACCENT if r == 0 else ERROR
            self.panel.app.refresh_calibration_status()
        confirm("Stick calibration", "Write the measured %s calibration to the controller?" % self.stick_name(),
                lambda: self.panel.run(lambda jc: calibration.save_stick(jc, left, cal), done))

    def use_factory(self):
        left = self.left

        def done(r):
            self.wizard.reset()
            self.refresh()
            self.info.text = "Done. The %s uses its factory calibration." % self.stick_name() if r == 0 else "Failed to write."
            self.info.color = ACCENT if r == 0 else ERROR
            self.panel.app.refresh_calibration_status()
        confirm("Stick calibration", "Erase the %s user calibration? The controller goes back to its factory calibration." % self.stick_name(),
                lambda: (self.stop(), self.panel.run(lambda jc: calibration.factory_stick(jc, left), done)))

    def refresh(self):
        w = self.wizard
        s = w.step
        self.steps.text = (mark(s > w.CENTER, s == w.CENTER) + "1. Center\n      Let go of the stick.\n\n" +
                           mark(s > w.ROTATE, s == w.ROTATE) + "2. Range\n      Rotate it slowly along the edge,\n      pushed all the way (2-3 turns).\n\n" +
                           mark(False, s == w.DONE) + "3. Save\n      Write it to the controller.")
        self.steps.color = TEXT if s == w.IDLE else ACCENT
        v = ""
        if w.x >= 0:
            v = "Stick:   X %03X  Y %03X\n" % (w.x, w.y)
        if s >= w.ROTATE:
            v += "Center: X %03X  Y %03X\nX range: %03X - %03X\nY range: %03X - %03X\n" % (
                w.center[0], w.center[1], w.min_x, w.max_x, w.min_y, w.max_y)
        if s == w.ROTATE:
            v += "Directions: %d / %d" % (w.sectors_done(), w.SECTORS)
        self.values.text = v
        if w.problem:
            self.info.text, self.info.color = w.problem, WARN
        elif s == w.IDLE:
            self.info.text, self.info.color = "Measures the %s's center and range and writes them as its user calibration. Click Start." % self.stick_name(), TEXT
        elif s == w.CENTER:
            self.info.text, self.info.color = ("Waiting for the controller.." if w.x < 0 else
                                               "Hold still.." if len(w.recent) > 1 else "Let go of the stick so it rests at its center."), TEXT
        elif s == w.ROTATE:
            self.info.text, self.info.color = ("Rotate the stick along its edge until the whole ring lights up." if w.sectors_done() < w.SECTORS
                                               else "Every direction covered. Rotate once or twice more, then click Finish."), TEXT
        elif s == w.DONE:
            self.info.text, self.info.color = "Measured. Click Save to write it to the controller (Start: measure again).", TEXT
        self.btn_start.text = "Start" if s == w.IDLE else "Restart"
        self.btn_finish.disabled = not w.can_finish
        self.btn_save.disabled = s != w.DONE
        self.view.draw()


class MotionView(Widget):
    """Bubble level, the turn (two positions) and the progress."""

    def __init__(self, wizard, **kw):
        super().__init__(**kw)
        self.wizard = wizard
        self.bind(pos=self.draw, size=self.draw)

    def draw(self, *_):
        w = self.wizard
        self.canvas.clear()
        cx = self.center_x
        r = min(self.width, self.height - dp(50)) / 2 - dp(16)
        cy = self.y + dp(50) + r + dp(10)
        one_g = w.ONE_G
        with self.canvas:
            Color(*DARK)
            Rectangle(pos=self.pos, size=self.size)
            Color(*GRID)
            Line(circle=(cx, cy, r))
            Line(circle=(cx, cy, dp(18)))
            Line(points=[cx - r, cy, cx + r, cy])
            Line(points=[cx, cy - r, cx, cy + r])
            if w.step in (w.TURN, w.MEASURE_B) or (w.step == w.DONE and w.two_positions):
                Color(*GRID)
                Line(circle=(cx, cy, r + dp(9), 0, 180), width=dp(3))
                sweep = min(200.0, abs(w.turned))
                Color(*(ACCENT if w.turn_in_range() else WARN))
                Line(circle=(cx, cy, r + dp(9), 0, sweep), width=dp(3))
            if w.have_sample:
                bx = max(-1.0, min(1.0, w.last[1] / (one_g / 2.0)))
                by = max(-1.0, min(1.0, -w.last[0] / (one_g / 2.0)))
                level = abs(w.last[0]) < one_g / 20 and abs(w.last[1]) < one_g / 20
                Color(*(ACCENT if level else WARN))
                Ellipse(pos=(cx + bx * (r - dp(12)) - dp(12), cy + by * (r - dp(12)) - dp(12)), size=(dp(24), dp(24)))
            Color(*GRID)
            Rectangle(pos=(self.x + dp(16), self.y + dp(20)), size=(self.width - dp(32), dp(12)))
            Color(*ACCENT)
            Rectangle(pos=(self.x + dp(16), self.y + dp(20)), size=((self.width - dp(32)) * w.progress, dp(12)))


class MotionTab(BoxLayout):
    def __init__(self, panel):
        super().__init__(orientation="horizontal", spacing=dp(10))
        self.panel = panel
        self.wizard = MotionWizard()
        self.measuring = False
        self.event = None
        self.view = MotionView(self.wizard, size_hint_x=0.5)
        self.add_widget(self.view)
        right = BoxLayout(orientation="vertical", spacing=dp(6), size_hint_x=0.5)
        self.two = Check("Two positions: turn it 180° (more accurate)", False, self.set_two)
        right.add_widget(self.two)
        self.steps = Lbl("", valign="top", size_hint_y=0.42, font_size=dp(13))
        right.add_widget(self.steps)
        self.values = Lbl("", valign="top", font_size=dp(13), size_hint_y=0.3)
        right.add_widget(self.values)
        self.info = Lbl("", valign="top", size_hint_y=0.28)
        right.add_widget(self.info)
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        self.btn_start = Btn("Start", self.start)
        self.btn_turned = Btn("Confirm turn", self.confirm_turn)
        self.btn_factory = Btn("Use factory", self.use_factory, color=WARN)
        self.btn_save = Btn("Save", self.save)
        for b in (self.btn_start, self.btn_turned, self.btn_factory, self.btn_save):
            row.add_widget(b)
        right.add_widget(row)
        self.add_widget(right)
        self.refresh()

    def set_two(self, on):
        self.stop()
        self.wizard.two_positions = on
        self.wizard.reset()
        self.refresh()

    def start(self):
        self.stop()
        self.wizard.start()
        self.measuring = True

        def work(jc):
            jc._subcmd_simple(0x40, 0x01)       # IMU on
            jc.set_input_report_mode(0x30)
            while self.measuring and self.wizard.step != self.wizard.DONE:
                n, r = jc.read(49, 20)
                if n > 12 and r[0] == 0x30:
                    self.wizard.add_report(r)
                jc.ui.poll()
            jc.set_input_report_mode(0x3F)
            jc._subcmd_simple(0x40, 0x00)
        if self.panel.run(work, lambda r: self.stop_refresh(), long_running=True, name="the motion calibration"):
            self.event = Clock.schedule_interval(lambda dt: self.refresh(), 0.05)

    def stop(self):
        self.measuring = False

    def stop_refresh(self):
        if self.event is not None:
            self.event.cancel()
            self.event = None
        self.refresh()

    def confirm_turn(self):
        self.wizard.confirm_turn()
        self.refresh()

    def save(self):
        res = self.wizard.result
        if res is None:
            return

        def done(r):
            self.info.text = "Saved. The controller now uses this calibration." if r == 0 else "Failed to write the calibration."
            self.info.color = ACCENT if r == 0 else ERROR
            self.panel.app.refresh_calibration_status()
        confirm("Motion calibration", "Write the measured motion calibration to the controller?",
                lambda: self.panel.run(lambda jc: calibration.save_motion(jc, res), done))

    def use_factory(self):
        def done(r):
            self.wizard.reset()
            self.refresh()
            self.info.text = "Done. The controller uses its factory motion calibration." if r == 0 else "Failed to write."
            self.info.color = ACCENT if r == 0 else ERROR
            self.panel.app.refresh_calibration_status()
        confirm("Motion calibration", "Erase the motion user calibration? The controller goes back to its factory calibration.",
                lambda: (self.stop(), self.panel.run(calibration.factory_motion, done)))

    def refresh(self):
        w = self.wizard
        s = w.step
        if not w.two_positions:
            t = (mark(s > w.IDLE, s == w.IDLE) + "1. Place it flat and level, buttons facing up.\n\n" +
                 mark(s == w.DONE, s == w.MEASURE_A) + "2. Don't touch it (~2 seconds).\n\n" +
                 mark(False, s == w.DONE) + "3. Save.")
        else:
            t = (mark(s > w.IDLE, s == w.IDLE) + "1. Place it flat, buttons up.\n" +
                 mark(s > w.MEASURE_A, s == w.MEASURE_A) + "2. Don't touch it (~2 s).\n" +
                 mark(s > w.TURN, s == w.TURN) + "3. Turn it 180° on the surface, still flat,\n      then click Confirm turn.\n" +
                 mark(s == w.DONE, s == w.MEASURE_B) + "4. Don't touch it (~2 s).\n" +
                 mark(False, s == w.DONE) + "5. Save.")
        self.steps.text = t
        self.steps.color = TEXT if s == w.IDLE else ACCENT
        self.two.disabled = s not in (w.IDLE, w.DONE)
        v = ""
        if w.have_sample:
            v = "Acc:   %6d %6d %6d\nGyro: %6d %6d %6d\n" % tuple(w.last)
        if s in (w.TURN, w.MEASURE_B):
            v += "Turned: %d°\n" % round(abs(w.turned))
        if w.result:
            v += "Offsets found:\nAcc:   %6d %6d %6d\nGyro: %6d %6d %6d" % tuple(w.result)
        self.values.text = v
        if s == w.IDLE:
            self.info.text, self.info.color = ("Measures the motion sensors' offsets (fixes drift) and writes them as the "
                                               "6-axis user calibration. Place the controller, then click Start."), TEXT
        elif s in (w.MEASURE_A, w.MEASURE_B):
            self.info.text = "Waiting for the controller.." if not w.have_sample else w.problem or "Measuring, don't touch it.."
            self.info.color = WARN if w.problem else TEXT
        elif s == w.TURN:
            self.info.text, self.info.color = (("Turned 180°. Let go of the controller, then click Confirm turn." if w.turn_in_range() else
                                                "Turn the controller around 180°, keeping it flat on the surface (%d° so far)." % round(abs(w.turned))), TEXT)
        elif s == w.DONE:
            self.info.text, self.info.color = "Measured. Click Save to write it to the controller (Start: measure again).", TEXT
        self.btn_start.text = "Start" if s == w.IDLE else "Restart"
        self.btn_turned.opacity = 1 if w.two_positions else 0
        self.btn_turned.disabled = not w.can_confirm_turn
        self.btn_save.disabled = s != w.DONE
        self.view.draw()


class ManualTab(BoxLayout):
    """The original editor: user calibration values and stick device parameters."""

    def __init__(self, panel):
        super().__init__(orientation="horizontal", spacing=dp(10))
        self.panel = panel
        left = BoxLayout(orientation="vertical", spacing=dp(8))
        self.sticks = {}
        for side, title in (("left", "Left analog stick (hex)"), ("right", "Right analog stick (hex)")):
            s = Section(title, size_hint_y=None, height=dp(150))
            grid = GridLayout(cols=4, spacing=dp(4), size_hint_y=None, height=dp(72))
            grid.add_widget(Lbl("", size_hint_x=0.2))
            for h in ("Minimum", "Center", "Maximum"):
                grid.add_widget(Lbl(h, color=DIM, font_size=dp(12)))
            fields = []
            for axis in ("X", "Y"):
                grid.add_widget(Lbl(axis, size_hint_x=0.2))
                for _ in range(3):
                    f = Field("")
                    fields.append(f)
                    grid.add_widget(f)
            s.add_widget(grid)
            chk = Check("Enable %s stick user calibration" % side, False)
            s.add_widget(chk)
            self.sticks[side] = (fields, chk, s)
            left.add_widget(s)
        left.add_widget(Widget())
        self.add_widget(left)

        right = BoxLayout(orientation="vertical", spacing=dp(8))
        s = Section("Acc/Gyro calibration (origins, Int16)", size_hint_y=None, height=dp(150))
        grid = GridLayout(cols=4, spacing=dp(4), size_hint_y=None, height=dp(72))
        self.sensor = []
        for label in ("Acc", "Gyro"):
            grid.add_widget(Lbl(label, size_hint_x=0.3))
            for _ in range(3):
                f = Field("0")
                self.sensor.append(f)
                grid.add_widget(f)
        s.add_widget(grid)
        self.chk_sensor = Check("Enable 6-axis user calibration", False)
        s.add_widget(self.chk_sensor)
        right.add_widget(s)
        p = Section("Stick device parameters (factory values!)", color=ERROR, size_hint_y=None, height=dp(150))
        grid = GridLayout(cols=3, spacing=dp(4), size_hint_y=None, height=dp(72))
        grid.add_widget(Lbl(""))
        grid.add_widget(Lbl("Deadzone", color=DIM, font_size=dp(12)))
        grid.add_widget(Lbl("Range ratio", color=DIM, font_size=dp(12)))
        self.params = []
        for label in ("Main stick", "Pro Con right"):
            grid.add_widget(Lbl(label))
            for _ in range(2):
                f = Field("")
                self.params.append(f)
                grid.add_widget(f)
        p.add_widget(grid)
        p.add_widget(Btn("Write params", self.write_params, color=ERROR))
        right.add_widget(p)
        right.add_widget(Lbl("Warning: the stick device parameters are factory values. Change them only when you have "
                             "drifting problems.", color=ERROR, font_size=dp(12), size_hint_y=None, height=dp(40)))
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        row.add_widget(Btn("Refresh all", self.refresh_all))
        row.add_widget(Btn("Write cal", self.write_cal, color=WARN))
        right.add_widget(row)
        right.add_widget(Widget())
        self.add_widget(right)

    def on_device(self):
        t = self.panel.jc.type
        self.sticks["left"][2].disabled = t == JOYCON_R
        self.sticks["right"][2].disabled = t == JOYCON_L
        for f in self.params[2:]:
            f.disabled = t != PROCON

    def refresh_all(self):
        def show(info):
            if isinstance(info, Exception):
                return
            for side, cal in (("left", info.user_left()), ("right", info.user_right())):
                fields, chk, _ = self.sticks[side]
                chk.active = cal is not None
                c = cal or StickCal()
                for f, v in zip(fields, c.as_list()):
                    f.text = "%03X" % (v & 0xFFF)
            valid = info.user_sensor_valid()
            self.chk_sensor.active = valid
            vals = info.sensor_values(True) if valid else [0] * 12
            for f, v in zip(self.sensor, vals[0:3] + vals[6:9]):
                f.text = str(v)
            for f, v in zip(self.params, info.stick_params() + info.stick_params(True)):
                f.text = "%X" % v
        self.panel.run(calibration.read_all, show)

    def _stick(self, side):
        fields, chk, _ = self.sticks[side]
        if not chk.active:
            return None
        return StickCal.from_list([int(f.text, 16) & 0xFFF for f in fields])

    def write_cal(self):
        try:
            left, right = self._stick("left"), self._stick("right")
            sensor = [int(f.text) for f in self.sensor] if self.chk_sensor.active else None
        except ValueError:
            message("Calibration", "Stick values are hex (000-FFF), 6-axis origins are numbers (-32768 to 32767).")
            return

        def done(r):
            message("Calibration", "The user calibration was written to SPI!" if r == 0 else
                    "Failed to write user calibration to SPI!\n\nPlease try again..")
            self.panel.app.refresh_calibration_status()
        confirm("Calibration", "Are you sure you want to continue?",
                lambda: self.panel.run(lambda jc: calibration.write_user_calibration(jc, left, right, sensor), done))

    def write_params(self):
        try:
            vals = [int(f.text, 16) & 0xFFF for f in self.params if not f.disabled]
        except ValueError:
            message("Calibration", "The parameters are hex (000-FFF).")
            return
        main = tuple(vals[0:2])
        second = tuple(vals[2:4]) if len(vals) == 4 else None

        def done(r):
            message("Calibration", "The stick parameters were written!" if r == 0 else "Failed to write the stick parameters!")
        confirm("Warning!", "These are stick device parameters coming from factory.\n\nAre you sure you want to continue?",
                lambda: self.panel.run(lambda jc: calibration.write_stick_params(jc, main, second), done))


class CalibrationPanel(Panel):
    def __init__(self, app):
        super().__init__(app, orientation="vertical", spacing=dp(6))
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(4))
        self.pages = ScreenManager(transition=NoTransition())
        self.tabs = {}
        self.sticks = SticksTab(self)
        self.motion = MotionTab(self)
        self.manual = ManualTab(self)
        for name, widget in (("Sticks", self.sticks), ("Motion", self.motion), ("Manual", self.manual)):
            s = Screen(name=name)
            s.add_widget(widget)
            self.pages.add_widget(s)
            tab = Tab(name, "calibration", size_hint_x=None, width=dp(140))
            tab.bind(on_press=lambda t, n=name: self.select(n))
            self.tabs[name] = tab
            row.add_widget(tab)
        row.add_widget(Widget())
        self.add_widget(row)
        self.add_widget(self.pages)
        self.select("Sticks")

    def select(self, name):
        self.sticks.stop()
        self.motion.stop()
        self.pages.current = name
        for n, t in self.tabs.items():
            t.state = "down" if n == name else "normal"

    def on_leave(self):
        self.sticks.stop()
        self.motion.stop()

    def on_device(self):
        self.sticks.on_device()
        self.manual.on_device()
