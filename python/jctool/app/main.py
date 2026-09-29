"""The Joy-Con Toolkit app (Kivy): runs on Windows, macOS, Linux and Android.

    python -m jctool.app            the controller(s) found
    python -m jctool.app --demo     an emulated controller (no hardware)
    python -m jctool.app -d         also write traffic_log.txt
"""
import argparse
import os
import sys

os.environ.setdefault("KIVY_NO_ARGS", "1")

from kivy.app import App                                   # noqa: E402
from kivy.clock import Clock                               # noqa: E402
from kivy.core.window import Window                        # noqa: E402
from kivy.metrics import dp                                # noqa: E402
from kivy.uix.boxlayout import BoxLayout                   # noqa: E402
from kivy.uix.gridlayout import GridLayout                 # noqa: E402
from kivy.uix.screenmanager import NoTransition, Screen, ScreenManager   # noqa: E402
from kivy.uix.scrollview import ScrollView                 # noqa: E402

from .. import calibration, ops                            # noqa: E402
from ..core import JoyCon, Ui                              # noqa: E402
from ..hidio import Device, JOYCON_L, JOYCON_R, PROCON, TYPE_NAMES, enumerate_controllers, is_android   # noqa: E402
from .widgets import (ACCENT, BACK, DARK, DIM, ERROR, TEXT, WARN, Btn, Filled, Lbl, Tab, Choice,   # noqa: E402
                      confirm, message)
from .worker import DeviceWorker                           # noqa: E402
from . import storage                                      # noqa: E402


PANEL_MIN = (920, 480)       # dp: screens smaller than this scroll (phones)


class AppUi(Ui):
    """The core's display hooks, forwarded to the Kivy thread (they're called on the device
    thread). poll() lets queued commands run inside long operations."""

    def __init__(self, app):
        self.app = app

    def _post(self, name, *args):
        screen = self.app.active_panel
        fn = getattr(screen, "on_" + name, None) if screen is not None else None
        if fn is not None:
            Clock.schedule_once(lambda dt: fn(*args), 0)

    def poll(self):
        self.app.worker.poll()

    def spi_progress(self, offset):
        self._post("spi_progress", offset)

    def custom_command(self, sent, reply_cmd, reply):
        self._post("custom_command", sent, reply_cmd, reply)

    def button_test_info(self, text):
        self._post("button_test_info", text)

    def button_test(self, report, sensors, raw):
        self._post("button_test", report, sensors, raw)

    def ir_status(self, text):
        self._post("ir_status", text)

    def ir_help(self, text):
        self._post("ir_help", text)

    def ir_frame(self, image, width, height):
        self._post("ir_frame", image, width, height)

    def ir_exposure(self, exposure):
        self._post("ir_exposure", exposure)

    def nfc_uid(self, text):
        self._post("nfc_uid", text)

    def nfc_tag(self, text):
        self._post("nfc_tag", text)

    def ntag_contents(self, data, pages):
        self._post("ntag_contents", data, pages)


class InfoRow(BoxLayout):
    def __init__(self, label, value="", **kw):
        super().__init__(orientation="horizontal", size_hint_y=None, height=dp(26), **kw)
        self.add_widget(Lbl(label, size_hint_x=0.38, font_size=dp(15)))
        self.value = Lbl(value, color=ACCENT, font_size=dp(15))
        self.add_widget(self.value)


class JoyConToolkitApp(App):
    title = "Joy-Con Toolkit v5.2.0"
    icon = os.path.join(os.path.dirname(os.path.abspath(__file__)), "icon.png")

    def __init__(self, demo=False, traffic_log=False, **kw):
        super().__init__(**kw)
        self.demo = demo
        self.traffic_log = traffic_log
        self.worker = DeviceWorker()
        self.jc = None
        self.fake = None
        self.panels = {}
        self.active_panel = None
        self.temp_celsius = True

    # ------------------------------------------------------------------------------------
    def build(self):
        from . import panels
        Window.clearcolor = BACK
        if not is_android() and not os.environ.get("JCTOOL_WINDOW"):
            Window.minimum_width, Window.minimum_height = 960, 680
            if Window.width < 1000:
                Window.size = (1040, 720)
        if os.environ.get("JCTOOL_WINDOW"):          # WxH: try other screen sizes (phones)
            Window.size = tuple(int(v) for v in os.environ["JCTOOL_WINDOW"].split("x"))
        root = Filled(orientation="vertical", color=BACK, padding=[dp(10), dp(8), dp(10), 0], spacing=dp(6))

        # Info section
        info = GridLayout(cols=2, size_hint_y=None, height=dp(84), spacing=[dp(20), 0])
        self.lbl_sn = InfoRow("S/N:")
        self.lbl_fw = InfoRow("FW Version:")
        self.lbl_mac = InfoRow("MAC:")
        self.lbl_dev = InfoRow("Controller:")
        self.lbl_cal = InfoRow("Calibration:")
        cal_link = BoxLayout(size_hint_y=None, height=dp(26))
        cal_link.add_widget(Lbl(""))
        cal_link.add_widget(Btn("Calibrate..", lambda: self.show("Calibration"), color=WARN,
                                size_hint_x=None, width=dp(120), height=dp(26)))
        for w in (self.lbl_sn, self.lbl_fw, self.lbl_mac, self.lbl_dev, self.lbl_cal, cal_link):
            info.add_widget(w)
        root.add_widget(info)

        # Tabs and screens
        self.tabs = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(3))
        self.screens = ScreenManager(transition=NoTransition(), size_hint=(None, None))
        # Small screens (phones): the screens scroll, the tabs and the status bar stay
        area = ScrollView(do_scroll_x=True, do_scroll_y=True, bar_width=dp(5), bar_color=ACCENT,
                          scroll_type=["bars", "content"])

        def fit(*_):
            self.screens.size = (max(area.width, dp(PANEL_MIN[0])), max(area.height, dp(PANEL_MIN[1])))
        area.bind(size=fit)
        area.add_widget(self.screens)
        for name, cls in panels.PANELS:
            panel = cls(self)
            self.panels[name] = panel
            s = Screen(name=name)
            s.add_widget(panel)
            self.screens.add_widget(s)
            tab = Tab(name, "screens")
            tab.bind(on_press=lambda t, n=name: self.show(n))
            self.tabs.add_widget(tab)
        root.add_widget(self.tabs)
        root.add_widget(area)

        # Status bar
        bar = Filled(orientation="horizontal", size_hint_y=None, height=dp(30), color=DARK,
                     padding=[dp(6), 0], spacing=dp(10))
        bar.add_widget(Btn("Connect / Refresh", self.connect, color=WARN, size_hint_x=None, width=dp(150), height=dp(30)))
        bar.add_widget(Btn("Disconnect", self.disconnect_controller, color=ERROR, size_hint_x=None, width=dp(110), height=dp(30)))
        self.lbl_status = Lbl("", color=DIM)
        bar.add_widget(self.lbl_status)
        self.lbl_link = Lbl("Link idle", size_hint_x=None, width=dp(110), halign="right")
        self.lbl_temp = Btn("", self.toggle_temperature, color=TEXT, size_hint_x=None, width=dp(80), height=dp(30))
        self.lbl_batt = Lbl("", size_hint_x=None, width=dp(170), halign="right")
        for w in (self.lbl_link, self.lbl_temp, self.lbl_batt):
            bar.add_widget(w)
        root.add_widget(bar)

        self.show(panels.PANELS[0][0])
        Clock.schedule_interval(lambda dt: self.update_link(), 1.0)
        Clock.schedule_once(lambda dt: self.connect(), 0.2)
        shots = os.environ.get("JCTOOL_SCREENSHOTS")
        if shots:
            Clock.schedule_once(lambda dt: self._screenshots(shots, [n for n, _ in panels.PANELS]), 2.0)
        return root

    def _screenshots(self, folder, names):
        """JCTOOL_SCREENSHOTS=folder: shows every screen, saves a picture of each, exits."""
        if not names:
            self.stop()
            return
        name = names[0]
        self.show(name)

        def shoot(dt):
            Window.screenshot(name=os.path.join(folder, name.replace(" ", "_") + ".png"))
            self._screenshots(folder, names[1:])
        Clock.schedule_once(shoot, 0.6)

    def show(self, name):
        if self.active_panel is not None and self.active_panel is not self.panels[name]:
            self.active_panel.on_leave()
        self.screens.current = name
        for tab in self.tabs.children:
            tab.state = "down" if tab.text == name else "normal"
        self.active_panel = self.panels[name]
        self.active_panel.on_show()

    def on_stop(self):
        for p in self.panels.values():
            p.on_leave()
        if self.jc is not None:
            self.jc.dev.close()
        self.worker.stop()

    # ------------------------------------------------------------------------------------
    # Connection
    def set_status(self, text, color=DIM):
        self.lbl_status.text = text
        self.lbl_status.color = color

    def connect(self):
        if self.worker.busy_long:
            self.set_status("Busy: stop %s first" % self.worker.busy_long, WARN)
            return
        if self.demo:
            if self.jc is None:
                from ..fake import FakeJoyCon
                self.fake = FakeJoyCon(self.demo)
                self.open_device(self.fake.device())
            else:
                self.refresh()
            return
        found = enumerate_controllers()
        if not found:
            self.set_status("No controller found. Bluetooth: pair it, allow root access. USB: plug it in (OTG)." if is_android()
                            else "No Joy-Con or Pro Controller found. Pair it, then Connect.", WARN)
            return
        if len(found) == 1:
            return self._open_found(found[0])
        from .panels import choose_controller
        choose_controller(found, self._open_found)

    def _open_found(self, f):
        def go():
            try:
                dev = Device.open(f)
            except Exception as e:
                hint = ("" if is_android() else "\n\nOn Linux, see the README (udev rule, hid_nintendo)."
                        if sys.platform.startswith("linux") else "")
                message("Can't open the controller", "%s\n\n%s%s" % (f.name, e, hint))
                return
            self.open_device(dev)
        if f.third_party:
            confirm("Third-party device", 'A potential third-party device has been detected:\n\n%s\n\n'
                    'Editing could be potentially unstable. Would you like to use this device anyways?' % f.name, go)
        else:
            go()

    def open_device(self, dev):
        if self.jc is not None:
            self.jc.dev.close()
        if self.traffic_log:
            dev.enable_traffic_log(storage.data_path("traffic_log.txt"))
        self.jc = JoyCon(dev, AppUi(self))
        self.worker.jc = self.jc

        def setup(jc):
            jc.silence_input_report()
            jc.set_led_busy()
            jc.send_rumble()
            return True
        self.worker.run(setup, lambda r: self.refresh())

    def refresh(self):
        """full_refresh: S/N, FW, MAC, type, battery, temperature, colors, calibration."""
        if self.jc is None:
            return

        def read(jc):
            info = ops.device_info(jc)
            sn = jc.get_sn() if jc.type != PROCON else "Not supported"
            return {"info": info, "sn": sn, "battery": ops.battery(jc), "temp": ops.temperature_c(jc),
                    "colors": ops.read_colors(jc), "cal": calibration.read_status(jc)}
        self.worker.run(read, self._refreshed)

    def _refreshed(self, r):
        if isinstance(r, Exception) or r is None:
            self.set_status("The controller didn't answer: %s" % r, ERROR)
            return
        self.lbl_sn.value.text = r["sn"]
        self.lbl_fw.value.text = r["info"].fw
        self.lbl_mac.value.text = r["info"].mac
        self.lbl_dev.value.text = TYPE_NAMES[self.jc.type] + (" (emulated)" if self.demo else "")
        self.set_calibration_status(r["cal"])
        self.show_battery(r["battery"])
        self.temperature = r["temp"]
        self.show_temperature()
        self.colors = r["colors"]
        self.set_status("Connected", ACCENT)
        for p in self.panels.values():
            p.on_device()

    def set_calibration_status(self, status):
        self.lbl_cal.value.text = status[0]

    def refresh_calibration_status(self):
        self.worker.run(lambda jc: calibration.read_status(jc), lambda r: None if isinstance(r, Exception) else self.set_calibration_status(r))

    def show_battery(self, b):
        volt, pct, state, _ = b
        self.lbl_batt.text = "%.2fV - %d%%" % (volt, pct)
        self.lbl_batt.color = ERROR if pct <= 10 else WARN if pct <= 30 else ACCENT

    def update_battery(self):
        self.worker.run(ops.battery, lambda r: None if isinstance(r, Exception) else self.show_battery(r))

    def show_temperature(self):
        t = getattr(self, "temperature", None)
        if t is None:
            return
        self.lbl_temp.text = ("%.1f°C" % t) if self.temp_celsius else ("%.1f°F" % (t * 1.8 + 32))

    def toggle_temperature(self):
        self.temp_celsius = not self.temp_celsius
        self.show_temperature()

    def disconnect_controller(self):
        if self.jc is None:
            return
        def go():
            self.worker.run(lambda jc: ops.disconnect(jc), lambda r: self.set_status("Disconnected", WARN))
        confirm("Disconnect", "Disconnect the controller (it turns off its Bluetooth connection)?", go)

    def update_link(self):
        if self.jc is None:
            self.lbl_link.text = "No link"
            return
        s = self.jc.dev.take_link_stats()
        errors = s.errors + s.write_errors
        if s.reports == 0 and s.writes == 0 and errors == 0:
            self.lbl_link.text = "Link idle"
            self.lbl_link.color = TEXT
            return
        rate = int(round(s.reports * 1000.0 / s.interval_ms)) if s.interval_ms else s.reports
        self.lbl_link.text = ("Link: %d err" % errors) if errors else "%d/s %dms" % (rate, s.longest_gap_ms)
        self.lbl_link.color = ERROR if errors else WARN if s.longest_gap_ms > 100 else TEXT

    def need_device(self):
        if self.jc is None:
            message("No controller", "Connect a Joy-Con or a Pro Controller first.")
            return False
        return True


def main(argv=None):
    p = argparse.ArgumentParser(description="Joy-Con Toolkit")
    p.add_argument("--demo", nargs="?", const="r", choices=["l", "r", "pro"],
                   help="use an emulated controller (default: Joy-Con (R))")
    p.add_argument("-d", action="store_true", help="write traffic_log.txt")
    p.add_argument("--selftest", action="store_true", help="drive the screens against an emulated Joy-Con (R) and exit")
    args = p.parse_args(argv)
    demo = {"l": JOYCON_L, "r": JOYCON_R, "pro": PROCON}[args.demo] if args.demo else False
    if args.selftest:
        demo = JOYCON_R
    app = JoyConToolkitApp(demo=demo, traffic_log=args.d)
    app.exit_code = 0
    if args.selftest:
        from .selftest import SelfTest
        app.selftest = SelfTest(app)
        Clock.schedule_once(lambda dt: app.selftest.start(), 0.5)
    app.run()
    sys.exit(app.exit_code)


if __name__ == "__main__":
    main()
