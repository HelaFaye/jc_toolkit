"""The IR Camera screen (Joy-Con (R)): capture, stream, live settings."""
import os

from kivy.graphics.texture import Texture
from kivy.metrics import dp
from kivy.uix.boxlayout import BoxLayout
from kivy.uix.gridlayout import GridLayout
from kivy.uix.image import Image
from kivy.uix.scrollview import ScrollView
from kivy.uix.widget import Widget

from .. import ops
from ..hidio import JOYCON_R
from ..png import write_png_rgb
from .panels import Panel
from .widgets import ACCENT, DARK, DIM, ERROR, TEXT, WARN, Btn, Check, Choice, Field, Filled, Lbl, Section, SliderRow, message


class IrPanel(Panel):
    def __init__(self, app):
        super().__init__(app)
        self.s = ops.IrSettings()
        self.streaming = False
        self.last_image = None
        self.capture_mode = "capture"

        scroll = ScrollView(size_hint_x=0.56, do_scroll_x=False)
        form = BoxLayout(orientation="vertical", spacing=dp(6), size_hint_y=None, padding=[0, 0, dp(10), 0])
        form.bind(minimum_height=form.setter("height"))
        s = self.s

        def setter(name):
            return lambda v: setattr(s, name, v)

        top = Section("Mode", size_hint_y=None, height=dp(3 * 38 + 32))
        grid = GridLayout(cols=2, spacing=dp(6), size_hint_y=None, height=dp(3 * 38))
        grid.add_widget(Lbl("Resolution"))
        self.res = Choice(ops.IrSettings.RESOLUTIONS, 0, setter("resolution"))
        grid.add_widget(self.res)
        grid.add_widget(Lbl("Mode"))
        grid.add_widget(Choice(ops.IrSettings.MODES, 0, setter("mode")))
        grid.add_widget(Lbl("Colorize"))
        grid.add_widget(Choice(ops.IrSettings.COLORS, 2, self.set_colorize))
        top.add_widget(grid)
        form.add_widget(top)

        leds = Section("Near-infrared light", size_hint_y=None, height=dp(8 * 32 + 30))
        leds.add_widget(Check("Far/Narrow (75°) leds 1/2", True, setter("leds_far")))
        leds.add_widget(SliderRow("Intensity", 0, 15, 15, setter("intensity_far")))
        leds.add_widget(Check("Near/Wide (130°) leds 3/4", True, setter("leds_near")))
        leds.add_widget(SliderRow("Intensity", 0, 16, 16, setter("intensity_near")))
        leds.add_widget(Check("Flashlight mode", False, setter("flashlight")))
        leds.add_widget(Check("Strobe flash mode", False, setter("strobe")))
        leds.add_widget(Check("External IR filter", True, setter("ex_filter")))
        leds.add_widget(Check("Selfie mode (flip)", False, setter("selfie")))
        form.add_widget(leds)

        exp = Section("Exposure", size_hint_y=None, height=dp(6 * 32 + 30))
        self.exposure = SliderRow("Exposure (us)", 0, 600, 300, setter("exposure"))
        exp.add_widget(self.exposure)
        exp.add_widget(Check("Auto exposure (stream)", False, setter("auto_exposure")))
        exp.add_widget(Check("Quick capture (no auto exposure, faster)", False,
                             lambda v: setattr(self.jc, "ir_quick_capture", v) if self.jc else None))
        exp.add_widget(SliderRow("Digital gain (lossy)", 1, 20, 2, setter("gain")))
        exp.add_widget(Lbl("Capture always adjusts the exposure (unless Quick capture). Gain applies to streams "
                           "without auto exposure.", color=DIM, font_size=dp(12)))
        form.add_widget(exp)

        dn = Section("De-noise", size_hint_y=None, height=dp(3 * 32 + 30))
        dn.add_widget(Check("Enable", True, setter("denoise")))
        dn.add_widget(SliderRow("Edge smoothing", 0, 255, 35, setter("edge_smoothing")))
        dn.add_widget(SliderRow("Color interpolation", 0, 255, 68, setter("color_interpolation")))
        form.add_widget(dn)

        reg = Section("IR sensor register (live)", size_hint_y=None, height=dp(2 * 36 + 30))
        row = BoxLayout(size_hint_y=None, height=dp(32), spacing=dp(6))
        row.add_widget(Lbl("Page+reg (hex)"))
        self.reg = Field("0")
        row.add_widget(self.reg)
        row.add_widget(Lbl("Value (hex)"))
        self.val = Field("0")
        row.add_widget(self.val)
        reg.add_widget(row)
        form.add_widget(reg)
        scroll.add_widget(form)
        self.add_widget(scroll)

        right = BoxLayout(orientation="vertical", spacing=dp(6), size_hint_x=0.44)
        frame = Filled(color=DARK)
        self.image = Image(fit_mode="contain", opacity=0)      # Shown with the first frame
        frame.add_widget(self.image)
        right.add_widget(frame)
        self.help = Lbl("", color=TEXT, font_size=dp(12), size_hint_y=None, height=dp(34))
        right.add_widget(self.help)
        self.status = Lbl("Status: Standby", color=WARN, size_hint_y=None, height=dp(24))
        right.add_widget(self.status)
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        self.btn_capture = Btn("Capture", self.capture)
        self.btn_stream = Btn("Stream", self.stream_or_stop, color=ERROR)
        self.btn_live = Btn("Apply live", self.apply_live, color=WARN, disabled=True)
        for b in (self.btn_capture, self.btn_stream, self.btn_live):
            row.add_widget(b)
        right.add_widget(row)
        right.add_widget(Lbl("Captures are saved as IRcamera.png (in the folder the app runs from).",
                             color=DIM, font_size=dp(12), size_hint_y=None, height=dp(22)))
        self.add_widget(right)

    def on_device(self):
        self.disabled = self.jc.type != JOYCON_R

    def set_colorize(self, i):
        self.s.colorize = i
        if self.last_image:
            self.on_ir_frame(*self.last_image, save=False)

    def _result(self, res):
        self.streaming = False
        self.btn_stream.text = "Stream"
        self.btn_capture.disabled = False
        self.btn_live.disabled = True
        self.res.disabled = False
        if isinstance(res, Exception):
            self.status.text = "Status: Error: %s" % res
        elif res == 10:
            self.status.text = "Status: Camera didn't apply the settings. Try again"
        elif res:
            self.status.text = "Status: Error %s!" % ops.IR_ERRORS.get(res, str(res))
        else:
            self.status.text = "Status: Standby" if self.capture_mode == "stream" else "Status: Done! Saved to IRcamera.png"

    def capture(self):
        self.capture_mode = "capture"
        s = self.s
        if self.run(lambda jc: ops.ir_run(jc, s, False), self._result, long_running=True, name="the IR camera"):
            self.btn_capture.disabled = True
            self.status.text = "Status: Configuring (a capture takes up to ~30s at 240x320)"

    def stream_or_stop(self):
        if self.streaming:
            self.jc.enable_ir_video = False
            self.btn_stream.disabled = True
            return
        self.capture_mode = "stream"
        s = self.s
        if self.run(lambda jc: ops.ir_run(jc, s, True), lambda r: (setattr(self.btn_stream, "disabled", False), self._result(r)),
                    long_running=True, name="the IR camera"):
            self.streaming = True
            self.btn_stream.text = "Stop"
            self.btn_capture.disabled = True
            self.btn_live.disabled = False
            self.res.disabled = True

    def apply_live(self):
        if not self.streaming:
            return
        try:
            self.s.custom_reg = int(self.reg.text or "0", 16) & 0xFFFF
            self.s.custom_val = int(self.val.text or "0", 16) & 0xFF
        except ValueError:
            message("IR", "The register and value are hex.")
            return
        self.jc.ir_pending_live = self.s.config(self.jc, False, True)

    def on_leave(self):
        if self.jc is not None and self.streaming:
            self.jc.enable_ir_video = False

    def on_ir_status(self, text):
        self.status.text = text

    def on_ir_help(self, text):
        self.help.text = text

    def on_ir_exposure(self, exposure):
        self.exposure.value = exposure

    def on_ir_frame(self, image, width, height, save=True):
        self.last_image = (image, width, height)
        rgb, w, h = ops.ir_render(image, width, height, self.s.colorize)
        tex = Texture.create(size=(w, h), colorfmt="rgb")
        tex.blit_buffer(rgb, colorfmt="rgb", bufferfmt="ubyte")
        tex.flip_vertical()
        self.image.texture = tex
        self.image.opacity = 1
        if save and not self.streaming:
            write_png_rgb("IRcamera.png", rgb, w, h)
