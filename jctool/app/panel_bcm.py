"""The BlueCubeMod screen: a GameCube controller as a Pro Controller (an ESP32 running
BlueCubeMod v3 or later). Its state, and its settings: how the analog triggers map, and how
the sticks are centered."""
from kivy.metrics import dp
from kivy.uix.boxlayout import BoxLayout
from kivy.uix.widget import Widget

from .. import bluecubemod
from .panels import Panel
from .widgets import ACCENT, DIM, ERROR, WARN, Btn, Choice, Lbl, Section, SliderRow, message


class BlueCubeModPanel(Panel):
    def __init__(self, app):
        super().__init__(app, orientation="vertical", spacing=dp(6))
        s = Section("BlueCubeMod")
        self.status = Lbl("Not a BlueCubeMod.", color=DIM, size_hint_y=None, height=dp(44))
        s.add_widget(self.status)
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        row.add_widget(Lbl("Triggers (L / R):", size_hint_x=0.25))
        self.triggers = Choice(bluecubemod.TRIGGER_MODES, on_change=self.trigger_changed)
        row.add_widget(self.triggers)
        s.add_widget(row)
        self.threshold = SliderRow("Analog threshold (fully pressed: about 200)", 1, 255, 0x80)
        s.add_widget(self.threshold)
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        row.add_widget(Lbl("Sticks:", size_hint_x=0.25))
        self.sticks = Choice(bluecubemod.STICK_MODES)
        row.add_widget(self.sticks)
        s.add_widget(row)
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        row.add_widget(Btn("Apply", self.apply, color=WARN))
        row.add_widget(Btn("Read again", self.on_device))
        s.add_widget(row)
        s.add_widget(Lbl("The Switch's L and R: the third trigger mode (a light press). Z is R; with Z held, "
                         "Start is -, D-pad up is HOME and down is Capture.\n"
                         "Re-centered sticks (the default) take where they rest at power-on as the center, "
                         "like a GameCube; the calibration (the Calibration screen, or the Switch's) sets "
                         "their range. Raw sticks use the calibration's center too.",
                         color=DIM, size_hint_y=None, height=dp(96)))
        s.add_widget(Widget())
        self.add_widget(s)
        self.disabled = True

    def trigger_changed(self, i):
        self.threshold.disabled = i == 0

    def on_device(self, *_):
        if self.jc is None:
            return
        if getattr(self.jc, "bcm", None) is None:
            self.status.text, self.status.color = "Not a BlueCubeMod.", DIM
            self.disabled = True
            return
        self.disabled = False
        self.run(bluecubemod.detect, self.show)

    def show(self, info):
        if isinstance(info, Exception) or info is None:
            self.status.text, self.status.color = "The BlueCubeMod didn't answer.", ERROR
            return
        self.jc.bcm = info
        self.status.text, self.status.color = info.describe(), ACCENT if info.gc_connected else WARN
        self.triggers.index = info.trigger_mode
        self.threshold.value = info.threshold
        self.sticks.index = info.stick_mode
        self.trigger_changed(info.trigger_mode)

    def apply(self):
        mode, threshold, sticks = self.triggers.index, self.threshold.value, self.sticks.index

        def done(res):
            if isinstance(res, Exception) or res is None:
                message("BlueCubeMod", "The BlueCubeMod didn't answer.")
            elif res == 0:
                self.app.set_status("BlueCubeMod settings saved", ACCENT)
                self.on_device()
            elif res == 2:
                message("BlueCubeMod", "The settings are applied, but the BlueCubeMod couldn't save them.")
            else:
                message("BlueCubeMod", "The BlueCubeMod refused these settings.")
        self.run(lambda jc: bluecubemod.set_settings(jc, mode, threshold, sticks), done)
