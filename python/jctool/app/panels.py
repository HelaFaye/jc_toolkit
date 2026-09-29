"""The app's screens: Device (colors, LEDs, rumble), Backup / Restore, Serial, Button test,
NFC and Debug. Calibration, HD Rumble and IR Camera are in their own modules."""
import os

from kivy.clock import Clock
from kivy.graphics import Color, Ellipse, Line, Rectangle, RoundedRectangle
from kivy.metrics import dp
from kivy.uix.boxlayout import BoxLayout
from kivy.uix.colorpicker import ColorPicker
from kivy.uix.gridlayout import GridLayout
from kivy.uix.popup import Popup
from kivy.uix.progressbar import ProgressBar
from kivy.uix.scrollview import ScrollView
from kivy.uix.widget import Widget

from .. import ops
from ..hidio import JOYCON_L, JOYCON_R, PROCON, TYPE_NAMES, list_hid_devices
from . import storage
from .widgets import (ACCENT, BACK, BUTTON, DARK, DIM, ERROR, GRID, TEXT, WARN, Btn, Check, Choice, Field,
                      Filled, Lbl, Mono, Section, confirm, message, rgba)


class Panel(BoxLayout):
    """A screen. on_show / on_leave (stop what runs) / on_device (a controller connected)."""

    def __init__(self, app, **kw):
        kw.setdefault("orientation", "horizontal")
        kw.setdefault("spacing", dp(10))
        kw.setdefault("padding", [0, dp(6), 0, dp(6)])
        super().__init__(**kw)
        self.app = app

    @property
    def jc(self):
        return self.app.jc

    def run(self, fn, done=None, long_running=False, name=""):
        if not self.app.need_device():
            return False
        self.app.worker.run(fn, done, long_running, name)
        return True

    def on_show(self):
        pass

    def on_leave(self):
        pass

    def on_device(self):
        pass


def choose_controller(found, on_chosen):
    box = Filled(orientation="vertical", padding=dp(10), spacing=dp(6), color=BACK)
    popup = Popup(title="Select a controller", content=box, size_hint=(0.6, 0.6), title_color=ACCENT,
                  separator_color=ACCENT, background="", background_color=DARK)
    for f in found:
        text = f.name + ("  (%s)" % f.serial if f.serial else "")
        box.add_widget(Btn(text, lambda f=f: (popup.dismiss(), on_chosen(f))))
    box.add_widget(Widget())
    popup.open()


# ----------------------------------------------------------------------------------------
class ControllerPreview(Widget):
    """The controller in its colors (body, buttons, grips)."""

    def __init__(self, **kw):
        super().__init__(**kw)
        self.type = JOYCON_R
        self.colors = ((70, 70, 70), (40, 40, 40), (70, 70, 70), (70, 70, 70))
        self.bind(pos=self.draw, size=self.draw)

    def set(self, type_, colors):
        self.type = type_
        if colors:
            self.colors = colors
        self.draw()

    def draw(self, *_):
        self.canvas.clear()
        body, buttons, lgrip, rgrip = [rgba(*c) for c in self.colors]
        cx, cy = self.center
        with self.canvas:
            if self.type == PROCON:
                w, h = min(self.width * 0.9, dp(420)), min(self.height * 0.75, dp(230))
                x0, y0 = cx - w / 2, cy - h / 2
                Color(*lgrip)
                RoundedRectangle(pos=(x0, y0), size=(w * 0.3, h * 0.8), radius=[dp(40)])
                Color(*rgrip)
                RoundedRectangle(pos=(x0 + w * 0.7, y0), size=(w * 0.3, h * 0.8), radius=[dp(40)])
                Color(*body)
                RoundedRectangle(pos=(x0 + w * 0.08, y0 + h * 0.3), size=(w * 0.84, h * 0.7), radius=[dp(50)])
                Color(*buttons)
                for (bx, by) in ((0.72, 0.8), (0.79, 0.7), (0.72, 0.6), (0.65, 0.7)):
                    Ellipse(pos=(x0 + w * bx - dp(11), y0 + h * by - dp(11)), size=(dp(22), dp(22)))
                Color(*rgba(30, 30, 30))
                for (sx, sy) in ((0.28, 0.72), (0.62, 0.48)):
                    Ellipse(pos=(x0 + w * sx - dp(22), y0 + h * sy - dp(22)), size=(dp(44), dp(44)))
            else:
                w, h = min(self.width * 0.75, dp(360)), min(self.height * 0.6, dp(170))
                x0, y0 = cx - w / 2, cy - h / 2
                Color(*body)
                RoundedRectangle(pos=(x0, y0), size=(w, h), radius=[dp(18), dp(18), dp(80), dp(80)])
                Color(*rgba(30, 30, 30))
                stick_x = x0 + (w * 0.33 if self.type == JOYCON_R else w * 0.62)
                Ellipse(pos=(stick_x - dp(26), y0 + h * 0.52 - dp(26)), size=(dp(52), dp(52)))
                Color(*buttons)
                bx0 = x0 + (w * 0.68 if self.type == JOYCON_R else w * 0.25)
                for (dx, dy) in ((0, 0.24), (0.09, 0), (0, -0.24), (-0.09, 0)):
                    Ellipse(pos=(bx0 + w * dx - dp(12), y0 + h * (0.52 + dy) - dp(12)), size=(dp(24), dp(24)))
            Color(*GRID)
            Line(rectangle=(self.x + 1, self.y + 1, self.width - 2, self.height - 2))


class DevicePanel(Panel):
    """The main screen: the controller in its colors, writing colors, LEDs, rumble."""

    def __init__(self, app):
        super().__init__(app)
        self.preview = ControllerPreview(size_hint_x=0.58)
        self.add_widget(self.preview)
        side = BoxLayout(orientation="vertical", spacing=dp(8), size_hint_x=0.42)

        colors = Section("Device colors", size_hint_y=None, height=dp(22 + 4 * 38 + 34 + 20 + 30))
        self.fields = {}
        grid = GridLayout(cols=3, spacing=dp(6), size_hint_y=None, height=dp(4 * 38))
        for key, label in (("body", "Body"), ("buttons", "Buttons"), ("lgrip", "Left grip"), ("rgrip", "Right grip")):
            grid.add_widget(Lbl(label))
            f = Field("")
            self.fields[key] = f
            grid.add_widget(f)
            grid.add_widget(Btn("Pick..", lambda k=key: self.pick(k), size_hint_x=None, width=dp(70)))
        colors.add_widget(grid)
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        row.add_widget(Btn("Read colors", self.read_colors, color=TEXT))
        row.add_widget(Btn("Write colors", self.write_colors, color=WARN))
        colors.add_widget(row)
        colors.add_widget(Lbl("Make an SPI backup first (Backup tab).", color=DIM, size_hint_y=None, height=dp(20), font_size=dp(12)))
        side.add_widget(colors)

        misc = Section("Controller", size_hint_y=None, height=dp(22 + 2 * 38 + 20))
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        self.led = Choice(["Player 1", "Player 2", "Player 3", "Player 4", "Players 1-4", "Flash 1", "Off"])
        row.add_widget(self.led)
        row.add_widget(Btn("Set LEDs", self.set_leds))
        misc.add_widget(row)
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        row.add_widget(Btn("Test rumble", lambda: self.run(lambda jc: jc.send_rumble())))
        row.add_widget(Btn("Battery", self.app.update_battery, color=TEXT))
        misc.add_widget(row)
        side.add_widget(misc)
        side.add_widget(Widget())
        self.add_widget(side)

    def on_device(self):
        t = self.jc.type
        self.fields["lgrip"].disabled = self.fields["rgrip"].disabled = t != PROCON
        self.show_colors(getattr(self.app, "colors", None))

    def show_colors(self, colors):
        if not colors:
            return
        for key, c in zip(("body", "buttons", "lgrip", "rgrip"), colors):
            self.fields[key].text = ops.hex_color(c)
        self.preview.set(self.jc.type, colors)

    def read_colors(self):
        self.run(ops.read_colors, lambda r: None if isinstance(r, Exception) else self.show_colors(r))

    def pick(self, key):
        try:
            start = ops.parse_color(self.fields[key].text)
        except ValueError:
            start = (128, 128, 128)
        box = Filled(orientation="vertical", color=BACK, padding=dp(8), spacing=dp(6))
        picker = ColorPicker(color=rgba(*start))
        box.add_widget(picker)
        popup = Popup(title="Pick a color", content=box, size_hint=(0.8, 0.9), background="", background_color=DARK,
                      title_color=ACCENT, separator_color=ACCENT)

        def ok():
            self.fields[key].text = "#" + picker.hex_color[1:7].upper()
            popup.dismiss()
            self.update_preview()
        box.add_widget(Btn("OK", ok, size_hint_x=None, width=dp(100)))
        popup.open()

    def current_colors(self):
        return tuple(ops.parse_color(self.fields[k].text) for k in ("body", "buttons", "lgrip", "rgrip"))

    def update_preview(self):
        try:
            self.preview.set(self.jc.type if self.jc else JOYCON_R, self.current_colors())
        except ValueError:
            pass

    def write_colors(self):
        if not self.app.need_device():
            return
        try:
            body, buttons, lgrip, rgrip = self.current_colors()
        except ValueError:
            message("Colors", "Enter the colors as RRGGBB (e.g. #FF3C28).")
            return
        self.update_preview()

        def go():
            def done(r):
                if r == 0:
                    message("Colors", "The colors were written to the device!")
                    self.read_colors()
                else:
                    message("Colors", "Failed to write the colors to the device!")
            self.run(lambda jc: ops.write_colors(jc, body, buttons, lgrip, rgrip), done)
        confirm("Write colors", "Don't forget to make a backup first!\n\nAre you sure you want to continue?", go)

    def set_leds(self):
        pattern = [0x01, 0x02, 0x04, 0x08, 0x0F, 0x10, 0x00][self.led.index]
        self.run(lambda jc: jc.send_subcommand(0x30, bytes([pattern])))


# ----------------------------------------------------------------------------------------
class BackupPanel(Panel):
    """SPI backup, and restores from a backup (with the original's checks)."""

    def __init__(self, app):
        super().__init__(app)
        left = Section("Backup", size_hint_x=0.45)
        left.add_widget(Lbl("Saves the whole 512KB SPI flash to a file. Takes a few minutes.\n"
                            "Make one before changing anything.", color=DIM, size_hint_y=None, height=dp(50)))
        left.add_widget(Btn("Backup SPI..", self.backup))
        self.progress = ProgressBar(max=0x80000, size_hint_y=None, height=dp(20))
        left.add_widget(self.progress)
        self.progress_label = Lbl("", size_hint_y=None, height=dp(24))
        left.add_widget(self.progress_label)
        self.btn_cancel = Btn("Cancel", self.cancel, color=ERROR, disabled=True)
        left.add_widget(self.btn_cancel)
        left.add_widget(Widget())
        self.add_widget(left)

        right = Section("Restore", size_hint_x=0.55)
        right.add_widget(Btn("Load SPI backup..", self.load))
        self.loaded = Lbl("No backup loaded.", color=DIM, size_hint_y=None, height=dp(60), valign="top")
        right.add_widget(self.loaded)
        self.chk_l = Check("Left stick calibration", True)
        self.chk_r = Check("Right stick calibration", True)
        self.chk_s = Check("6-axis (acc/gyro) calibration", True)
        for c in (self.chk_l, self.chk_r, self.chk_s):
            right.add_widget(c)
        grid = GridLayout(cols=2, spacing=dp(6), size_hint_y=None, height=dp(3 * 40))
        self.btn_colors = Btn("Restore colors", lambda: self.restore("colors"))
        self.btn_sn = Btn("Restore S/N", lambda: self.restore("sn"))
        self.btn_cal = Btn("Restore user calibration", lambda: self.restore("cal"))
        self.btn_reset = Btn("Factory reset user calibration", lambda: self.restore("reset"), color=WARN)
        self.btn_full = Btn("Full restore", lambda: self.restore("full"), color=ERROR)
        for b in (self.btn_colors, self.btn_sn, self.btn_cal, self.btn_reset, self.btn_full):
            grid.add_widget(b)
        right.add_widget(grid)
        right.add_widget(Widget())
        self.add_widget(right)
        self.backup_data = None
        self.same_mac = False
        self.update_buttons()

    def on_device(self):
        t = self.jc.type
        self.chk_l.disabled = t == JOYCON_R
        self.chk_r.disabled = t == JOYCON_L
        self.update_buttons()

    def update_buttons(self):
        have = self.backup_data is not None
        for b in (self.btn_colors, self.btn_sn, self.btn_cal):
            b.disabled = not have
        self.btn_full.disabled = not (have and self.same_mac)

    def on_spi_progress(self, offset):
        self.progress.value = offset
        self.progress_label.text = "%.2fKB of 512KB" % (offset / 1024.0)

    def backup(self):
        if not self.app.need_device():
            return

        def chosen(path):
            if os.path.exists(path):
                confirm("Backup", "The file %s already exists!\n\nDo you want to overwrite it?" % os.path.basename(path),
                        lambda: self.dump(path))
            else:
                self.dump(path)
        self.run(ops.backup_filename, lambda name: storage.save_file("Save the SPI backup", name, chosen))

    def dump(self, path):
        self.btn_cancel.disabled = False

        def work(jc):
            jc.cancel_spi_dump = False
            jc.send_rumble()
            jc.set_led_busy()
            res = jc.dump_spi(path)
            cancelled = jc.cancel_spi_dump
            jc.cancel_spi_dump = False
            if res == 0 and not cancelled:
                jc.send_rumble()
            return res, cancelled

        def done(r):
            self.btn_cancel.disabled = True
            if isinstance(r, Exception):
                message("Backup", "Failed: %s" % r)
            elif r[1]:
                message("Backup", "Cancelled. %s is incomplete." % os.path.basename(path))
            elif r[0] == 0:
                message("Backup", "Done dumping SPI!\n\nSaved to %s" % path)
                storage.export(path)
            else:
                message("Backup", "Failed to dump the SPI chip!")
        self.run(work, done, long_running=True, name="the SPI backup")

    def cancel(self):
        if self.jc:
            self.jc.cancel_spi_dump = True

    def load(self):
        if not self.app.need_device():
            return

        def chosen(path):
            try:
                data = open(path, "rb").read()
            except OSError as e:
                message("Restore", "Cannot open %s\n\n%s" % (path, e))
                return

            def checked(mac):
                if isinstance(mac, Exception):
                    return
                b = ops.Backup(data)
                err, same = b.check(self.jc.type, mac)
                if err:
                    self.backup_data, self.same_mac = None, False
                    self.loaded.text = err
                    self.loaded.color = ERROR
                else:
                    self.backup_data, self.same_mac = b, same
                    self.loaded.text = "Backup of %s loaded.%s" % (b.mac_text(), "" if same else
                        "\nDifferent BT MAC address! The backup is from another %s: full restore is disabled." % TYPE_NAMES[self.jc.type])
                    self.loaded.color = ACCENT if same else WARN
                self.update_buttons()
            self.run(lambda jc: ops.device_info(jc).mac_bytes, checked)
        storage.open_file("Load an SPI backup", chosen, filters=["*.bin"])

    def restore(self, what):
        if not self.app.need_device():
            return
        b = self.backup_data
        jc_type = self.jc.type
        l, r, s = self.chk_l.active and jc_type != JOYCON_R, self.chk_r.active and jc_type != JOYCON_L, self.chk_s.active
        texts = {
            "colors": ("The device color will be restored with the backup values!", lambda jc: ops.restore_colors(jc, b)),
            "sn": ("The serial number will be restored with the backup values!\n*Make sure that this backup was your original one!",
                   lambda jc: ops.restore_sn(jc, b)),
            "cal": ("The selected user calibration will be restored from the backup!",
                    lambda jc: ops.restore_user_calibration(jc, b, l, r, s)),
            "reset": ("The selected user calibration will be factory resetted!",
                      lambda jc: ops.restore_user_calibration(jc, None, l, r, s, factory_reset=True)),
            "full": ("This will do a full restore of the Factory configuration and User calibration!\n\n"
                     "The controller reboots into pairing mode afterwards.", lambda jc: ops.full_restore(jc, b)),
        }
        text, fn = texts[what]

        def done(res):
            if res == 0:
                message("Restore", "Done!" if what != "full" else
                        "The full restore was completed!\n\nThe controller was rebooted and it is now in pairing mode. "
                        "Pair it with the Switch or PC again.")
                if what != "full":
                    self.app.refresh()
            else:
                message("Restore", "Failed to restore or restore incomplete! Please try again..")
        confirm("Restore", text + "\n\nAre you sure you want to continue?", lambda: self.run(fn, done))


# ----------------------------------------------------------------------------------------
class SerialPanel(Panel):
    def __init__(self, app):
        super().__init__(app, orientation="vertical")
        s = Section("Serial number")
        self.current = Lbl("", color=ACCENT, size_hint_y=None, height=dp(30))
        s.add_widget(self.current)
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        self.new_sn = Field("")
        row.add_widget(Lbl("New S/N (1-15 ASCII characters):", size_hint_x=0.45))
        row.add_widget(self.new_sn)
        row.add_widget(Btn("Change S/N", self.change, color=WARN, size_hint_x=0.25))
        s.add_widget(row)
        s.add_widget(Btn("Restore the S/N from the backup inside the controller", self.restore))
        s.add_widget(Lbl("The first change backs your original S/N up inside the controller's SPI flash. "
                         "Not supported on the Pro Controller.", color=DIM, size_hint_y=None, height=dp(44)))
        s.add_widget(Widget())
        self.add_widget(s)

    def on_device(self):
        self.current.text = "Current S/N: " + self.app.lbl_sn.value.text
        self.disabled = self.jc.type == PROCON

    def change(self):
        sn = self.new_sn.text.strip()
        if not ops.valid_sn(sn):
            message("S/N", "Use 1-15 non-extended ASCII characters.")
            return

        def go():
            confirm("S/N", "Did you make a backup?", lambda: self.run(lambda jc: (ops.change_sn(jc, sn), jc.get_sn()), done))

        def done(r):
            if isinstance(r, Exception) or r[0] != 0:
                message("S/N", "Failed to write the S/N to the device!")
            else:
                message("S/N", 'The S/N was written to the device! The new S/N is now "%s"!\n\n'
                               "A backup of your S/N was created inside the SPI." % r[1])
                self.app.refresh()
        confirm("S/N", "This will change your Serial Number! Make a backup first!\n\nAre you sure you want to continue?", go)

    def restore(self):
        def done(r):
            if isinstance(r, Exception):
                return
            res, sn = r
            if res == 2:
                message("S/N", "No S/N backup found inside your controller's SPI.\n\nThis can happen if the first time you "
                               "changed your S/N was with an older version of Joy-Con Toolkit. Otherwise, you never changed your S/N.")
            elif res == 0:
                message("S/N", 'The S/N was restored to the device! The new S/N is now "%s"!' % sn)
                self.app.refresh()
            else:
                message("S/N", "Failed to restore the S/N!")
        confirm("S/N", "Do you really want to restore it from the S/N backup inside your controller's SPI?",
                lambda: self.run(lambda jc: (ops.restore_sn_from_controller(jc), jc.get_sn()), done))


# ----------------------------------------------------------------------------------------
class StickView(Widget):
    """A stick's position (calibrated -1..1), for the button test."""

    def __init__(self, title, **kw):
        super().__init__(**kw)
        self.title = title
        self.pos_xy = (0.0, 0.0)
        self.bind(pos=self.draw, size=self.draw)

    def set(self, x, y):
        self.pos_xy = (x, y)
        self.draw()

    def draw(self, *_):
        self.canvas.clear()
        r = min(self.width, self.height) / 2 - dp(8)
        cx, cy = self.center
        with self.canvas:
            Color(*DARK)
            Rectangle(pos=self.pos, size=self.size)
            Color(*GRID)
            Line(circle=(cx, cy, r))
            Line(points=[cx - r, cy, cx + r, cy])
            Line(points=[cx, cy - r, cx, cy + r])
            Color(*ACCENT)
            Ellipse(pos=(cx + self.pos_xy[0] * r - dp(7), cy + self.pos_xy[1] * r - dp(7)), size=(dp(14), dp(14)))


class ButtonTestPanel(Panel):
    def __init__(self, app):
        super().__init__(app)
        left = BoxLayout(orientation="vertical", spacing=dp(6), size_hint_x=0.34)
        self.btn = Btn("Turn on", self.toggle)
        left.add_widget(self.btn)
        scroll = ScrollView()
        self.info = Mono("", size_hint_y=None)
        self.info.bind(texture_size=lambda *_: setattr(self.info, "height", self.info.texture_size[1] + dp(10)))
        scroll.add_widget(self.info)
        left.add_widget(scroll)
        self.add_widget(left)
        mid = BoxLayout(orientation="vertical", spacing=dp(6), size_hint_x=0.36)
        self.report = Mono("Turn on to see the buttons, sticks and 6-axis sensors live.")
        mid.add_widget(self.report)
        self.add_widget(mid)
        right = BoxLayout(orientation="vertical", spacing=dp(6), size_hint_x=0.3)
        self.sensors = Mono("", size_hint_y=0.5)
        right.add_widget(self.sensors)
        sticks = BoxLayout(spacing=dp(6), size_hint_y=0.5)
        self.stick_l = StickView("L")
        self.stick_r = StickView("R")
        sticks.add_widget(self.stick_l)
        sticks.add_widget(self.stick_r)
        right.add_widget(sticks)
        self.add_widget(right)

    def toggle(self):
        if self.jc is not None and self.jc.enable_button_test:
            self.jc.enable_button_test = False
            return

        def work(jc):
            jc.enable_button_test = True
            return jc.button_test()

        def done(r):
            self.btn.text = "Turn on"
        if self.run(work, done, long_running=True, name="the button test"):
            self.btn.text = "Turn off"

    def on_leave(self):
        if self.jc is not None:
            self.jc.enable_button_test = False

    def on_button_test_info(self, text):
        self.info.text = text.strip()

    def on_button_test(self, report, sensors, raw):
        self.report.text = report
        self.sensors.text = sensors
        for line_name, view in (("L Stick", self.stick_l), ("R Stick", self.stick_r)):
            i = report.find(line_name)
            if i < 0:
                continue
            try:
                cal_line = report[i:].split("\n")[3]
                parts = cal_line.replace("X:", "").replace("Y:", "").split()
                view.set(float(parts[0]), float(parts[1]))
            except (IndexError, ValueError):
                pass


# ----------------------------------------------------------------------------------------
class NfcPanel(Panel):
    def __init__(self, app):
        super().__init__(app, orientation="vertical")
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        self.btn = Btn("Scan", self.toggle, size_hint_x=None, width=dp(140))
        row.add_widget(self.btn)
        self.lbl_uid = Lbl("Touch the NFC area with an amiibo or an NFC tag.", color=ACCENT)
        row.add_widget(self.lbl_uid)
        self.add_widget(row)
        scroll = ScrollView()
        self.dump = Mono("", size_hint_y=None)
        self.dump.bind(texture_size=lambda *_: setattr(self.dump, "height", self.dump.texture_size[1] + dp(10)))
        scroll.add_widget(self.dump)
        self.add_widget(scroll)

    def on_device(self):
        self.disabled = self.jc.type == JOYCON_L

    def toggle(self):
        if self.jc is not None and self.jc.enable_nfc_scanning:
            self.jc.enable_nfc_scanning = False
            return

        def work(jc):
            jc.enable_nfc_scanning = True
            res = jc.nfc_tag_info()
            jc.enable_nfc_scanning = False
            return res

        def done(res):
            self.btn.text = "Scan"
            if isinstance(res, int) and res > 0 and res in ops.NFC_ERRORS:
                self.lbl_uid.text = "Error %s!" % ops.NFC_ERRORS[res]
        if self.run(work, done, long_running=True, name="the NFC scan"):
            self.btn.text = "Stop"
            self.dump.text = ""

    def on_leave(self):
        if self.jc is not None:
            self.jc.enable_nfc_scanning = False

    def on_nfc_uid(self, text):
        self.lbl_uid.text = text.replace("\n", "   ")

    def on_nfc_tag(self, text):
        self.dump.text = text

    def on_ntag_contents(self, data, pages):
        self.dump.text = ops.ntag_text(data, pages)


# ----------------------------------------------------------------------------------------
class DebugPanel(Panel):
    def __init__(self, app):
        super().__init__(app)
        left = Section("Custom command (hex)", size_hint_x=0.4)
        grid = GridLayout(cols=2, spacing=dp(6), size_hint_y=None, height=dp(7 * 38))
        self.fields = {}
        for key, label, default in (("cmd", "Command", "01"), ("hf", "Rumble HF frequency", "00"),
                                    ("ha", "Rumble HF amplitude", "01"), ("lf", "Rumble LF frequency", "40"),
                                    ("la", "Rumble LF amplitude", "40"), ("sub", "Subcommand", "02"),
                                    ("args", "Arguments", "")):
            grid.add_widget(Lbl(label))
            f = Field(default)
            self.fields[key] = f
            grid.add_widget(f)
        left.add_widget(grid)
        left.add_widget(Lbl("Command: 01 subcommand (with rumble), 10 rumble only, 11 MCU/IR/NFC.",
                            color=DIM, size_hint_y=None, height=dp(40), font_size=dp(12)))
        left.add_widget(Btn("Send", self.send, color=WARN))
        left.add_widget(Btn("List HID devices", self.list_hid, color=TEXT))
        self.log = Check("Traffic log (traffic_log.txt)", False, self.set_log)
        left.add_widget(self.log)
        left.add_widget(Widget())
        self.add_widget(left)
        scroll = ScrollView(size_hint_x=0.6)
        self.out = Mono("", size_hint_y=None)
        self.out.bind(texture_size=lambda *_: setattr(self.out, "height", self.out.texture_size[1] + dp(10)))
        scroll.add_widget(self.out)
        self.add_widget(scroll)

    def send(self):
        def hx(key, default=0):
            t = self.fields[key].text.strip()
            try:
                return int(t, 16) if t else default
            except ValueError:
                return default
        args = ops.debug_command_args(hx("cmd", 1), hx("hf"), hx("ha", 1), hx("lf", 0x40), hx("la", 0x40), hx("sub"),
                                      self.fields["args"].text)
        self.run(lambda jc: jc.send_custom_command(args))

    def on_custom_command(self, sent, reply_cmd, reply):
        self.out.text = "Sent:\n%s\n%s\n\n%s" % (sent, reply_cmd, reply)

    def list_hid(self):
        self.out.text = list_hid_devices()

    def set_log(self, on):
        if self.jc is None:
            return
        if on:
            self.jc.dev.enable_traffic_log(storage.data_path("traffic_log.txt"))
        elif self.jc.dev.traffic_log is not None:
            self.jc.dev.traffic_log.close()
            self.jc.dev.traffic_log = None


from .panel_calibration import CalibrationPanel   # noqa: E402
from .panel_ir import IrPanel                      # noqa: E402
from .panel_rumble import RumblePanel              # noqa: E402

PANELS = [
    ("Device", DevicePanel),
    ("Backup", BackupPanel),
    ("Serial", SerialPanel),
    ("Calibration", CalibrationPanel),
    ("Button test", ButtonTestPanel),
    ("HD Rumble", RumblePanel),
    ("IR Camera", IrPanel),
    ("NFC", NfcPanel),
    ("Debug", DebugPanel),
]
