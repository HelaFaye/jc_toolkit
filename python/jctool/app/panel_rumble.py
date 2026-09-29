"""The HD Rumble screen: Files (.jcvib / .bnvib, the equalizer, the tunes) and MIDI (a MIDI
file or device played on the rumble)."""
import math
import os

from kivy.clock import Clock
from kivy.graphics import Color, Rectangle
from kivy.metrics import dp
from kivy.uix.boxlayout import BoxLayout
from kivy.uix.screenmanager import NoTransition, Screen, ScreenManager
from kivy.uix.widget import Widget

from .. import midi, ops
from ..hidio import JOYCON_L
from .panels import Panel
from .widgets import (ACCENT, DARK, DIM, ERROR, GRID, TEXT, WARN, Btn, Check, Choice, Field, Lbl, Section,
                      SliderRow, Tab, choose_file, message)


class FilesTab(BoxLayout):
    def __init__(self, panel):
        super().__init__(orientation="horizontal", spacing=dp(10))
        self.panel = panel
        self.vib = None
        left = Section("HD Rumble file", size_hint_x=0.5)
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        row.add_widget(Btn("Load rumble..", self.load))
        self.btn_play = Btn("Play", self.play)
        row.add_widget(self.btn_play)
        left.add_widget(row)
        self.info = Lbl("No file loaded", color=WARN, valign="top", size_hint_y=None, height=dp(90))
        left.add_widget(self.info)
        row = BoxLayout(size_hint_y=None, height=dp(32), spacing=dp(6))
        row.add_widget(Lbl("Loop times (0-999):"))
        self.loops = Field("0", size_hint_x=0.4)
        row.add_widget(self.loops)
        left.add_widget(row)
        left.add_widget(Lbl("Easter egg tunes (hold the controller near your ear):", color=DIM, size_hint_y=None, height=dp(24)))
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        row.add_widget(Btn("Super Mario Bros.", lambda: self.tune(0)))
        row.add_widget(Btn('Super Mario Odyssey "OK"', lambda: self.tune(1)))
        left.add_widget(row)
        left.add_widget(Widget())
        self.add_widget(left)
        eq = Section("Equalizer (binary files; 10 = unchanged)", size_hint_x=0.5)
        self.eq = [SliderRow(label, 0, 20, 10) for label in ("Low freq amplitude", "Low freq pitch",
                                                             "High freq amplitude", "High freq pitch")]
        for s in self.eq:
            eq.add_widget(s)
        eq.add_widget(Btn("Reset", self.reset_eq, color=TEXT))
        eq.add_widget(Widget())
        self.add_widget(eq)

    def reset_eq(self):
        for s in self.eq:
            s.value = 10

    def load(self):
        def chosen(path):
            try:
                self.vib = ops.VibFile(open(path, "rb").read())
            except (OSError, ValueError) as e:
                self.vib = None
                self.info.text, self.info.color = "%s: %s" % (os.path.basename(path), e), ERROR
                return
            v = self.vib
            self.info.text = "%s\nType: %s\nSample rate: %dms\nSamples: %d (%.2fs)" % (
                os.path.basename(path), v.type_name, v.sample_rate, v.samples, v.seconds)
            self.info.color = ACCENT
        choose_file("Load an HD Rumble file", chosen, filters=["*.bnvib", "*.jcvib"])

    def play(self):
        jc = self.panel.jc
        if jc is not None and self.panel.app.worker.busy_long == "the HD Rumble":
            jc.stop_playback = True
            return
        v = self.vib
        if v is None:
            return
        try:
            v.loop_times = max(0, min(999, int(self.loops.text or "0")))
        except ValueError:
            v.loop_times = 0
        lf_amp, lf_freq, hf_amp, hf_freq = (s.value for s in self.eq)
        v.convert(lf_amp, lf_freq, hf_amp, hf_freq)

        def work(jc):
            jc.play_hd_rumble(v)
            jc.stop_playback = False
            return 0
        if self.panel.run(work, lambda r: setattr(self.btn_play, "text", "Play"), long_running=True, name="the HD Rumble"):
            self.btn_play.text = "Stop"

    def tune(self, n):
        def work(jc):
            jc.set_led_busy()
            jc.play_tune(n)
            jc.send_rumble()
        self.panel.run(work, long_running=True, name="the HD Rumble")

    def stop(self):
        if self.panel.jc is not None:
            self.panel.jc.stop_playback = True


class BandsView(Widget):
    """The high band's note (teal) and the low band's (orange), on their frequency ranges."""

    def __init__(self, rumble, **kw):
        super().__init__(**kw)
        self.rumble = rumble
        self.bind(pos=self.draw, size=self.draw)

    def draw(self, *_):
        self.canvas.clear()
        with self.canvas:
            Color(*DARK)
            Rectangle(pos=self.pos, size=self.size)
            self._band(self.top - dp(34), midi.HIGH_LOW, midi.HIGH_HIGH, self.rumble.high_note, ACCENT)
            self._band(self.top - dp(80), midi.LOW_LOW, midi.LOW_HIGH, self.rumble.low_note, WARN)

    def _band(self, y, lo, hi, pitch, color):
        x0, w = self.x + dp(10), self.width - dp(20)
        Color(*GRID)
        Rectangle(pos=(x0, y), size=(w, dp(8)))
        if pitch < 0:
            return
        hz = midi.fold(midi.frequency(pitch), lo, hi)
        x = x0 + (math.log(hz) - math.log(lo)) / (math.log(hi) - math.log(lo)) * w
        Color(*color)
        Rectangle(pos=(x - dp(3), y - dp(3)), size=(dp(6), dp(14)))


class MidiTab(BoxLayout):
    def __init__(self, panel):
        super().__init__(orientation="horizontal", spacing=dp(10))
        self.panel = panel
        self.rumble = midi.MidiRumble(self._send, None)
        self.player = midi.MidiFilePlayer(self.rumble)
        self.input = midi.MidiInput(self.rumble)
        self.song = None
        self.event = None

        left = BoxLayout(orientation="vertical", spacing=dp(8), size_hint_x=0.5)
        f = Section("MIDI file", size_hint_y=None, height=dp(170))
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        row.add_widget(Btn("Load MIDI..", self.load))
        self.btn_play = Btn("Play", self.play_or_stop)
        row.add_widget(self.btn_play)
        f.add_widget(row)
        self.file_info = Lbl("No MIDI file loaded (.mid).", size_hint_y=None, height=dp(40), valign="top")
        f.add_widget(self.file_info)
        self.part = Choice(["All parts (no drums)"])
        f.add_widget(self.part)
        left.add_widget(f)
        d = Section("MIDI device", size_hint_y=None, height=dp(170))
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
        self.device = Choice(["(none)"])
        row.add_widget(self.device)
        row.add_widget(Btn("Scan", self.scan, size_hint_x=None, width=dp(70)))
        d.add_widget(row)
        self.channel = Choice(["All channels"] + ["Channel %d" % c for c in range(1, 17)],
                              on_change=lambda i: setattr(self.input, "channel", i - 1))
        d.add_widget(self.channel)
        self.btn_listen = Btn("Listen", self.listen_or_stop)
        d.add_widget(self.btn_listen)
        left.add_widget(d)
        left.add_widget(Widget())
        self.add_widget(left)

        right = BoxLayout(orientation="vertical", spacing=dp(8), size_hint_x=0.5)
        right.add_widget(Lbl("High band", color=DIM, size_hint_y=None, height=dp(20)))
        self.bands = BandsView(self.rumble, size_hint_y=None, height=dp(100))
        right.add_widget(self.bands)
        self.notes = Lbl("", size_hint_y=None, height=dp(24))
        right.add_widget(self.notes)
        self.status = Lbl("", size_hint_y=None, height=dp(48), valign="top")
        right.add_widget(self.status)
        self.bass = Check("Lowest note on the low band", True, lambda v: setattr(self.rumble, "bass_on_low", v))
        right.add_widget(self.bass)
        self.volume = SliderRow("Volume", 0, 100, 100, lambda v: setattr(self.rumble, "volume", v / 100.0), fmt="{:d}%")
        right.add_widget(self.volume)
        right.add_widget(Lbl("The idea comes from Musical-Joycons by sarossilli. Two notes play at once: the highest on "
                             "the high band, the lowest on the low band.", color=DIM, font_size=dp(12)))
        self.add_widget(right)
        self.scan()
        self.refresh()

    def _send(self, rumble4):
        jc = self.panel.jc
        if jc is not None:
            jc.rumble_report(rumble4)       # Rumble-only reports get no reply: safe from this thread

    def _enable(self, on, then=None):
        self.panel.run(lambda jc: jc.enable_vibration(on), (lambda r: then()) if then else None)

    def load(self):
        def chosen(path):
            self.stop()
            try:
                self.song = midi.MidiSong.load(open(path, "rb").read())
            except (OSError, ValueError) as e:
                self.song = None
                self.file_info.text, self.file_info.color = "Can't load %s: %s" % (os.path.basename(path), e), WARN
                return
            s = self.song
            t = int(round(s.length))
            self.file_info.text = "%s\n%d:%02d, %d notes, %d parts" % (os.path.basename(path), t // 60, t % 60, len(s.notes), len(s.parts))
            self.file_info.color = TEXT
            self.part.values = ["All parts (no drums)"] + [str(p) for p in s.parts]
            self.part.index = 0
            self.refresh()
        choose_file("Open a MIDI file", chosen, filters=["*.mid", "*.midi", "*.MID"])

    def play_or_stop(self):
        if self.player.playing:
            self.stop()
            return
        if self.song is None or not self.panel.app.need_device():
            return
        self.input.stop()
        self._enable(True, lambda: (self.player.play(self.song, self.part.index - 1), self._start_ui()))

    def scan(self):
        self.midi_available = midi.MidiInput.available()
        names = midi.MidiInput.devices()
        self.device.values = names or ["(none)"]
        self.device.index = 0
        self.refresh()

    def listen_or_stop(self):
        if self.input.listening:
            self.stop()
            return
        name = self.device.text
        if name == "(none)" or not self.panel.app.need_device():
            return
        self.player.stop()

        def start():
            if self.input.listen(name):
                self._start_ui()
            self.refresh()
        self._enable(True, start)

    def _start_ui(self):
        if self.event is None:
            self.event = Clock.schedule_interval(lambda dt: self.refresh(), 0.05)
        self.refresh()

    def stop(self):
        was = self.player.playing or self.input.listening
        self.player.stop()
        self.input.stop()
        if was and self.panel.jc is not None:
            self._enable(False)
        if self.event is not None:
            self.event.cancel()
            self.event = None
        self.refresh()

    def refresh(self):
        playing, listening = self.player.playing, self.input.listening
        if not playing and not listening and self.event is not None:
            self.event.cancel()
            self.event = None
            self._enable(False)
        self.btn_play.text = "Stop" if playing else "Play"
        self.btn_play.disabled = self.song is None
        self.btn_listen.text = "Stop" if listening else "Listen"
        if self.input.error:
            self.status.text, self.status.color = self.input.error, WARN
        elif playing:
            p, t = int(self.player.position), int(round(self.song.length))
            self.status.text, self.status.color = "Playing %d:%02d / %d:%02d" % (p // 60, p % 60, t // 60, t % 60), TEXT
        elif listening:
            self.status.text, self.status.color = "Listening. Play something (%d messages)." % self.input.parser.messages, TEXT
        elif not self.midi_available:
            self.status.text, self.status.color = "MIDI devices need the mido and python-rtmidi packages.", DIM
        else:
            self.status.text, self.status.color = "Load a MIDI file and Play, or pick a MIDI device and Listen.", DIM
        self.notes.text = ("Notes: " + self.rumble.now_playing) if self.rumble.now_playing else ""
        self.bands.draw()


class RumblePanel(Panel):
    def __init__(self, app):
        super().__init__(app, orientation="vertical", spacing=dp(6))
        row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(4))
        self.pages = ScreenManager(transition=NoTransition())
        self.files = FilesTab(self)
        self.midi = MidiTab(self)
        self.tabs = {}
        for name, widget in (("Files", self.files), ("MIDI", self.midi)):
            s = Screen(name=name)
            s.add_widget(widget)
            self.pages.add_widget(s)
            tab = Tab(name, "rumble", size_hint_x=None, width=dp(140))
            tab.bind(on_press=lambda t, n=name: self.select(n))
            self.tabs[name] = tab
            row.add_widget(tab)
        row.add_widget(Widget())
        self.add_widget(row)
        self.add_widget(self.pages)
        self.select("Files")

    def select(self, name):
        self.files.stop()
        self.midi.stop()
        self.pages.current = name
        for n, t in self.tabs.items():
            t.state = "down" if n == name else "normal"

    def on_leave(self):
        self.files.stop()
        self.midi.stop()
