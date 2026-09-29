"""Dark theme widgets for the app (the original's look: grey panels, teal and orange text)."""
import os

from kivy.graphics import Color, Line, Rectangle
from kivy.metrics import dp
from kivy.uix.boxlayout import BoxLayout
from kivy.uix.button import Button
from kivy.uix.checkbox import CheckBox
from kivy.uix.filechooser import FileChooserListView
from kivy.uix.label import Label
from kivy.uix.popup import Popup
from kivy.uix.slider import Slider
from kivy.uix.spinner import Spinner, SpinnerOption
from kivy.uix.textinput import TextInput
from kivy.uix.togglebutton import ToggleButton


def rgba(r, g, b, a=255):
    return (r / 255.0, g / 255.0, b / 255.0, a / 255.0)


BACK = rgba(70, 70, 70)
DARK = rgba(55, 55, 55)
PANEL = rgba(62, 62, 62)
BUTTON = rgba(85, 85, 85)
BUTTON_ON = rgba(110, 110, 110)
GRID = rgba(95, 95, 95)
TEXT = rgba(251, 251, 251)
DIM = rgba(160, 160, 160)
ACCENT = rgba(9, 255, 206)
WARN = rgba(255, 188, 0)
ERROR = rgba(255, 60, 40)


class Btn(Button):
    def __init__(self, text="", on_press=None, color=ACCENT, **kw):
        kw.setdefault("size_hint_y", None)
        kw.setdefault("height", dp(34))
        super().__init__(text=text, background_normal="", background_down="", background_disabled_normal="",
                         background_color=BUTTON, color=color, **kw)
        self.disabled_color = DIM
        if on_press:
            self.bind(on_press=lambda *_: on_press())

    def on_state(self, *_):
        self.background_color = BUTTON_ON if self.state == "down" else BUTTON


class Tab(ToggleButton):
    def __init__(self, text, group, **kw):
        kw.setdefault("size_hint_y", None)
        kw.setdefault("height", dp(34))
        super().__init__(text=text, group=group, allow_no_selection=False, background_normal="",
                         background_down="", background_color=BUTTON, color=DIM, **kw)
        self.bind(state=self._restyle)

    def _restyle(self, *_):
        on = self.state == "down"
        self.background_color = BUTTON_ON if on else BUTTON
        self.color = ACCENT if on else DIM


class Lbl(Label):
    """A left-aligned label that wraps to its width."""

    def __init__(self, text="", color=TEXT, font_size=None, halign="left", valign="middle", wrap=True, **kw):
        if font_size is not None:
            kw["font_size"] = font_size
        super().__init__(text=text, color=color, halign=halign, valign=valign, **kw)
        if wrap:
            self.bind(size=lambda *_: setattr(self, "text_size", (self.width, self.height)))


class Mono(Lbl):
    """Monospaced text (reports, dumps)."""

    def __init__(self, text="", **kw):
        kw.setdefault("font_name", "RobotoMono-Regular")
        kw.setdefault("valign", "top")
        kw.setdefault("font_size", dp(12))
        super().__init__(text=text, **kw)


class Field(TextInput):
    def __init__(self, text="", **kw):
        kw.setdefault("size_hint_y", None)
        kw.setdefault("height", dp(32))
        kw.setdefault("multiline", False)
        super().__init__(text=text, background_normal="", background_active="", background_color=DARK,
                         foreground_color=WARN, cursor_color=TEXT, padding=[dp(6), dp(7)], **kw)


class Check(BoxLayout):
    """A checkbox with a label on its left."""

    def __init__(self, text, active=False, on_change=None, color=WARN, **kw):
        kw.setdefault("size_hint_y", None)
        kw.setdefault("height", dp(30))
        super().__init__(orientation="horizontal", spacing=dp(4), **kw)
        self.label = Lbl(text, color=color)
        self.box = CheckBox(active=active, size_hint_x=None, width=dp(30), color=TEXT)
        self.add_widget(self.label)
        self.add_widget(self.box)
        if on_change:
            self.box.bind(active=lambda _, v: on_change(v))

    @property
    def active(self):
        return self.box.active

    @active.setter
    def active(self, v):
        self.box.active = v


class SliderRow(BoxLayout):
    """label  [slider]  value"""

    def __init__(self, text, lo, hi, value, on_change=None, fmt="{:d}", step=1, **kw):
        kw.setdefault("size_hint_y", None)
        kw.setdefault("height", dp(30))
        super().__init__(orientation="horizontal", spacing=dp(6), **kw)
        self.fmt = fmt
        self.label = Lbl(text, size_hint_x=0.42)
        self.slider = Slider(min=lo, max=hi, value=value, step=step, size_hint_x=0.43,
                             value_track=True, value_track_color=ACCENT, cursor_size=(dp(16), dp(16)))
        self.value_label = Lbl(fmt.format(int(value)), color=WARN, size_hint_x=0.15)
        self.add_widget(self.label)
        self.add_widget(self.slider)
        self.add_widget(self.value_label)
        self.slider.bind(value=self._changed)
        self._on_change = on_change

    def _changed(self, _, v):
        self.value_label.text = self.fmt.format(int(v))
        if self._on_change:
            self._on_change(int(v))

    @property
    def value(self):
        return int(self.slider.value)

    @value.setter
    def value(self, v):
        self.slider.value = v


class DarkOption(SpinnerOption):
    def __init__(self, **kw):
        super().__init__(**kw)
        self.background_normal = ""
        self.background_color = BUTTON
        self.color = TEXT
        self.height = dp(32)


class Choice(Spinner):
    def __init__(self, values, index=0, on_change=None, **kw):
        kw.setdefault("size_hint_y", None)
        kw.setdefault("height", dp(32))
        values = list(values)
        super().__init__(text=values[index] if values else "", values=values, option_cls=DarkOption,
                         background_normal="", background_color=BUTTON, color=WARN, **kw)
        if on_change:
            self.bind(text=lambda *_: on_change(self.index))

    @property
    def index(self):
        try:
            return list(self.values).index(self.text)
        except ValueError:
            return -1

    @index.setter
    def index(self, i):
        if 0 <= i < len(self.values):
            self.text = self.values[i]


class Section(BoxLayout):
    """A titled group (the original's GroupBoxes)."""

    def __init__(self, title="", color=ACCENT, **kw):
        kw.setdefault("orientation", "vertical")
        kw.setdefault("padding", [dp(8), dp(2), dp(8), dp(8)])
        kw.setdefault("spacing", dp(4))
        super().__init__(**kw)
        with self.canvas.before:
            Color(*GRID)
            self._border = Line(rectangle=(0, 0, 1, 1), width=1)
        self.bind(pos=self._draw, size=self._draw)
        if title:
            self.title = Lbl(title, color=color, size_hint_y=None, height=dp(22))
            self.add_widget(self.title)

    def _draw(self, *_):
        self._border.rectangle = (self.x + 1, self.y + 1, self.width - 2, self.height - 2)


class Filled(BoxLayout):
    """A BoxLayout with a background color."""

    def __init__(self, color=BACK, **kw):
        super().__init__(**kw)
        with self.canvas.before:
            self._color = Color(*color)
            self._rect = Rectangle()
        self.bind(pos=self._draw, size=self._draw)

    def _draw(self, *_):
        self._rect.pos = self.pos
        self._rect.size = self.size


# ----------------------------------------------------------------------------------------
# Popups
def message(title, text, on_close=None):
    box = Filled(orientation="vertical", padding=dp(12), spacing=dp(10), color=BACK)
    box.add_widget(Lbl(text, valign="top"))
    popup = Popup(title=title, content=box, size_hint=(0.6, 0.45), separator_color=ACCENT,
                  title_color=ACCENT, background="", background_color=DARK)
    def close():
        popup.dismiss()
        if on_close:
            on_close()
    box.add_widget(Btn("OK", close, size_hint_x=None, width=dp(100), pos_hint={"right": 1}))
    popup.open()
    return popup


def confirm(title, text, on_yes, on_no=None, yes="Yes", no="No"):
    box = Filled(orientation="vertical", padding=dp(12), spacing=dp(10), color=BACK)
    box.add_widget(Lbl(text, valign="top"))
    row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(8))
    popup = Popup(title=title, content=box, size_hint=(0.6, 0.45), separator_color=WARN,
                  title_color=WARN, background="", background_color=DARK, auto_dismiss=False)

    def answer(ok):
        popup.dismiss()
        (on_yes if ok else (on_no or (lambda: None)))()
    row.add_widget(Lbl(""))
    row.add_widget(Btn(yes, lambda: answer(True), size_hint_x=None, width=dp(100)))
    row.add_widget(Btn(no, lambda: answer(False), size_hint_x=None, width=dp(100), color=TEXT))
    box.add_widget(row)
    popup.open()
    return popup


def choose_file(title, on_chosen, filters=None, save_name=None, path=None):
    """A file chooser popup. With save_name: pick a folder and a name to save as."""
    box = Filled(orientation="vertical", padding=dp(8), spacing=dp(6), color=BACK)
    chooser = FileChooserListView(path=path or os.path.expanduser("~"), filters=filters or [],
                                  dirselect=False)
    box.add_widget(chooser)
    name = None
    if save_name is not None:
        name = Field(save_name)
        box.add_widget(name)
    row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(8))
    popup = Popup(title=title, content=box, size_hint=(0.8, 0.85), separator_color=ACCENT,
                  title_color=ACCENT, background="", background_color=DARK)

    def ok():
        if save_name is not None:
            target = os.path.join(chooser.path, name.text.strip() or save_name)
        elif chooser.selection:
            target = chooser.selection[0]
        else:
            return
        popup.dismiss()
        on_chosen(target)
    row.add_widget(Lbl(""))
    row.add_widget(Btn("Save" if save_name is not None else "Open", ok, size_hint_x=None, width=dp(100)))
    row.add_widget(Btn("Cancel", popup.dismiss, size_hint_x=None, width=dp(100), color=TEXT))
    box.add_widget(row)
    chooser.bind(on_submit=lambda *_: ok())
    popup.open()
    return popup
