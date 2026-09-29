"""MIDI on the HD Rumble (the HD Rumble Player's MIDI tab).

The idea comes from Musical-Joycons by sarossilli (github.com/sarossilli/MusicalJoycons):
play MIDI notes as the frequency of the Joy-Con's rumble, octave-shifted into what the
actuator can do. This is a separate implementation that uses both HD Rumble bands:
  high band (81.75-1252 Hz): the highest note held, folded into 400-1252 Hz
  low band (40.875-626 Hz):  the lowest note held (when two or more are), folded into 100-626 Hz
Amplitudes come from the note velocities and the volume, encoded with the HD Rumble
Player's tables, and kept to a sum of 1.0 like it does.

Sources: Standard MIDI Files (format 0/1, tempo changes, SMPTE time), and MIDI devices
through mido + python-rtmidi (ALSA on Linux, CoreMIDI on macOS, Windows MIDI).
"""
import math
import threading
import time

from . import tables

NAMES = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]
HIGH_LOW, HIGH_HIGH = 400.0, 1252.0     # High band range used for notes
LOW_LOW, LOW_HIGH = 100.0, 626.0        # Low band range used for notes
SILENCE = b"\x00\x01\x40\x40"


def note_name(pitch):
    return NAMES[pitch % 12] + str(pitch // 12 - 1)


def frequency(pitch):
    return 440.0 * 2 ** ((pitch - 69) / 12.0)


def fold(hz, low, high):
    """Octave-shifts a frequency into [low, high]."""
    while hz < low:
        hz *= 2
    while hz > high:
        hz /= 2
    return hz


def amp_index(amp):
    if amp <= 0:
        return 0
    for j in range(1, 101):
        if amp < tables.AMP_FLOAT[j]:
            return j - 1
    return 100


def encode(hf, ha, lf, la):
    """HD Rumble encoding of one side (4 bytes). Frequencies: round(log2(f / 10) * 32), then
    high band (x - 0x60) * 4, low band x - 0x40. Amplitudes: the HD Rumble Player's tables."""
    hj, lj = amp_index(ha), amp_index(la)
    if hj == 0 and lj == 0:
        return SILENCE
    hf_code = int(round(math.log(hf / 10.0, 2) * 32)) if hf > 0 else 0xC0
    lf_code = int(round(math.log(lf / 10.0, 2) * 32)) if lf > 0 else 0x80
    hf_code = max(0x60, min(0xDF, hf_code))
    lf_code = max(0x40, min(0xBF, lf_code))
    hf_raw = (hf_code - 0x60) * 4
    lf_raw = lf_code - 0x40
    return bytes([hf_raw & 0xFF, (((hf_raw >> 8) & 0xFF) + tables.AMP_HA[hj]) & 0xFF,
                  (((tables.AMP_LA[lj] >> 8) & 0xFF) + lf_raw) & 0xFF, tables.AMP_LA[lj] & 0xFF])


def decode_high_hz(b):
    """The high band frequency of 4 encoded bytes."""
    hf_raw = b[0] | ((b[1] & 0x01) << 8)
    return 10 * 2 ** ((hf_raw // 4 + 0x60) / 32.0)


def choose(notes, volume, bass_on_low):
    """notes: [(pitch, velocity)] sorted by pitch. Returns (hf, ha, lf, la)."""
    if not notes:
        return 0.0, 0.0, 0.0, 0.0
    top = notes[-1]
    hf = fold(frequency(top[0]), HIGH_LOW, HIGH_HIGH)
    ha = top[1] / 127.0 * volume
    lf = la = 0.0
    if bass_on_low and len(notes) > 1:
        lf = fold(frequency(notes[0][0]), LOW_LOW, LOW_HIGH)
        la = notes[0][1] / 127.0 * volume
    total = ha + la
    if total > 1:
        ha, la = ha / total, la / total
    return hf, ha, lf, la


# ----------------------------------------------------------------------------------------
# MIDI files
class MidiPart:
    def __init__(self, track, channel, name):
        self.track, self.channel, self.name, self.notes = track, channel, name, 0

    @property
    def drums(self):
        return self.channel == 9

    def __str__(self):
        return "%s (ch %d%s, %d notes)" % (self.name or "Track %d" % self.track, self.channel + 1,
                                          ", drums" if self.drums else "", self.notes)


class MidiNote:
    __slots__ = ("start", "end", "pitch", "velocity", "part")

    def __init__(self, start, end, pitch, velocity, part):
        self.start, self.end, self.pitch, self.velocity, self.part = start, end, pitch, velocity, part


def _varlen(d, pos):
    v = 0
    for _ in range(4):
        if pos >= len(d):
            break
        b = d[pos]
        pos += 1
        v = (v << 7) | (b & 0x7F)
        if not b & 0x80:
            break
    return v, pos


class MidiSong:
    def __init__(self):
        self.notes = []
        self.parts = []
        self.length = 0.0

    @classmethod
    def load(cls, data):
        """Parses a Standard MIDI File (bytes)."""
        d = bytes(data)
        if len(d) < 14 or d[0:4] != b"MThd":
            raise ValueError("Not a MIDI file")
        header_len = int.from_bytes(d[4:8], "big")
        fmt, ntracks, division = (int.from_bytes(d[i:i + 2], "big") for i in (8, 10, 12))
        if fmt > 1:
            raise ValueError("MIDI format %d isn't supported" % fmt)
        pos = 8 + header_len
        tempos = []                    # (tick, us per quarter note)
        raw = []                       # (tick, order, track, status, data1, data2)
        names = {}
        for t in range(ntracks):
            if pos + 8 > len(d):
                break
            cid = d[pos:pos + 4]
            ln = int.from_bytes(d[pos + 4:pos + 8], "big")
            pos += 8
            end = min(len(d), pos + ln)
            if cid != b"MTrk":
                pos = end
                continue
            tick = 0
            status = 0
            while pos < end:
                delta, pos = _varlen(d, pos)
                tick += delta
                if pos >= end:
                    break
                b = d[pos]
                if b == 0xFF:                       # Meta event
                    mtype = d[pos + 1]
                    mlen, pos = _varlen(d, pos + 2)
                    if mtype == 0x51 and mlen == 3:
                        tempos.append((tick, int.from_bytes(d[pos:pos + 3], "big")))
                    elif mtype == 0x03 and t not in names:
                        names[t] = d[pos:pos + mlen].decode("utf-8", "replace").strip("\0 ")
                    elif mtype == 0x2F:
                        pos = end - mlen
                    pos += mlen
                    continue
                if b in (0xF0, 0xF7):               # SysEx
                    slen, pos = _varlen(d, pos + 1)
                    pos += slen
                    continue
                if b & 0x80:
                    status = b
                    pos += 1
                elif status == 0:
                    raise ValueError("Bad MIDI data in track %d" % t)
                kind = status & 0xF0
                d1 = d[pos]
                pos += 1
                d2 = 0
                if kind not in (0xC0, 0xD0):
                    d2 = d[pos]
                    pos += 1
                raw.append((tick, len(raw), t, status, d1, d2))
            pos = end

        tempos.sort(key=lambda x: x[0])
        if division & 0x8000:
            fps = 256 - (division >> 8)
            per_frame = division & 0xFF
            tick_s = 1.0 / (29.97 if fps == 29 else fps) / per_frame
            seconds = lambda tick: tick * tick_s
        else:
            tpq = max(1, division)

            def seconds(tick):
                s, at, us = 0.0, 0, 500000          # 120 BPM until the first tempo event
                for tt, tus in tempos:
                    if tt >= tick:
                        break
                    s += (tt - at) * us / float(tpq) / 1e6
                    at, us = tt, tus
                return s + (tick - at) * us / float(tpq) / 1e6

        song = cls()
        part_index = {}
        open_notes = {}
        raw.sort(key=lambda e: (e[0], e[1]))      # File order at the same tick
        for tick, _, track, st, d1, d2 in raw:
            kind, ch = st & 0xF0, st & 0x0F
            if kind not in (0x80, 0x90):
                continue
            key = (track, ch, d1)
            if kind == 0x90 and d2 > 0:
                open_notes.setdefault(key, []).append((tick, d2))
                continue
            starts = open_notes.get(key)
            if not starts:
                continue
            on_tick, vel = starts.pop()
            pk = (track, ch)
            if pk not in part_index:
                part_index[pk] = len(song.parts)
                song.parts.append(MidiPart(track, ch, names.get(track, "")))
            pi = part_index[pk]
            song.parts[pi].notes += 1
            n = MidiNote(seconds(on_tick), seconds(tick), d1, vel, pi)
            song.notes.append(n)
            song.length = max(song.length, n.end)
        # Parts in file order (track, then channel)
        order = sorted(range(len(song.parts)), key=lambda i: (song.parts[i].track, song.parts[i].channel))
        remap = {old: new for new, old in enumerate(order)}
        song.parts = [song.parts[i] for i in order]
        for n in song.notes:
            n.part = remap[n.part]
        song.notes.sort(key=lambda n: n.start)
        return song


# ----------------------------------------------------------------------------------------
class MidiRumble:
    """Held notes -> HD Rumble, sent from its own thread: on every change, and again every
    25ms while notes sound (a lost Bluetooth packet must not leave a wrong note playing).

    `send(bytes4)` sends one rumble report; `enable(on)` switches vibration on/off (the
    controller ignores rumble until it's enabled)."""

    def __init__(self, send, enable=None):
        self._send = send
        self._enable = enable
        self.held = {}
        self.sustained = set()
        self.sustain = False
        self.lock = threading.Lock()
        self.dirty = False
        self.running = False
        self.thread = None
        self.volume = 1.0
        self.bass_on_low = True
        self.now_playing = ""
        self.high_note = self.low_note = -1

    def start(self):
        if self.running:
            return
        with self.lock:
            self.held.clear()
            self.sustained.clear()
            self.sustain = False
            self.dirty = True
        if self._enable:
            self._enable(True)
        self.running = True
        self.thread = threading.Thread(target=self._run, name="MIDI rumble", daemon=True)
        self.thread.start()

    def stop(self):
        if not self.running:
            return
        self.running = False
        self.thread.join(0.5)
        self._send(SILENCE)
        if self._enable:
            self._enable(False)
        self.now_playing = ""
        self.high_note = self.low_note = -1

    def note_on(self, pitch, velocity):
        if velocity == 0:
            return self.note_off(pitch)
        with self.lock:
            self.held[pitch] = velocity
            self.sustained.discard(pitch)
            self.dirty = True

    def note_off(self, pitch):
        with self.lock:
            if self.sustain and pitch in self.held:
                self.sustained.add(pitch)
            else:
                self.held.pop(pitch, None)
            self.dirty = True

    def set_sustain(self, on):
        with self.lock:
            self.sustain = on
            if not on:
                for p in self.sustained:
                    self.held.pop(p, None)
                self.sustained.clear()
            self.dirty = True

    def all_off(self):
        with self.lock:
            self.held.clear()
            self.sustained.clear()
            self.dirty = True

    def _run(self):
        last_send = -1.0
        sounding = False
        notes = []
        while self.running:
            with self.lock:
                changed = self.dirty
                self.dirty = False
                if changed:
                    notes = sorted(self.held.items())
            now = time.monotonic()
            if changed or (sounding and now - last_send >= 0.025):
                self._send(encode(*choose(notes, self.volume, self.bass_on_low)))
                last_send = now
                sounding = bool(notes)
                if changed:
                    self.now_playing = " ".join(note_name(p) for p, _ in reversed(notes[-8:]))
                    self.high_note = notes[-1][0] if notes else -1
                    self.low_note = notes[0][0] if self.bass_on_low and len(notes) > 1 else -1
            time.sleep(0.001)


class MidiFilePlayer:
    """Plays a MidiSong's notes into a MidiRumble at their times."""

    def __init__(self, rumble):
        self.rumble = rumble
        self.thread = None
        self.running = False
        self.position = 0.0

    @property
    def playing(self):
        return self.running

    def play(self, song, part=-1):
        """part < 0: all parts except drums."""
        self.stop()
        events = []
        for n in song.notes:
            if (n.part != part) if part >= 0 else song.parts[n.part].drums:
                continue
            events.append((n.start, 1, n.pitch, n.velocity))
            events.append((n.end, 0, n.pitch, 0))
        events.sort(key=lambda e: (e[0], e[1]))    # Offs before ons at the same time
        self.running = True
        self.position = 0.0
        self.rumble.start()
        self.thread = threading.Thread(target=self._run, args=(events, song.length), name="MIDI file", daemon=True)
        self.thread.start()

    def stop(self):
        if self.thread is None:
            return
        self.running = False
        self.thread.join(1.0)
        self.thread = None
        self.rumble.stop()

    def _run(self, events, length):
        t0 = time.monotonic()
        held = {}
        i = 0
        while self.running and i < len(events):
            now = time.monotonic() - t0
            self.position = now
            while i < len(events) and events[i][0] <= now:
                when, on, pitch, vel = events[i]
                i += 1
                count = held.get(pitch, 0)
                if on:
                    held[pitch] = count + 1
                    self.rumble.note_on(pitch, vel)
                elif count > 0:
                    held[pitch] = count - 1
                    if count == 1:
                        self.rumble.note_off(pitch)
            if i < len(events):
                wait = events[i][0] - (time.monotonic() - t0)
                if wait > 0.002:
                    time.sleep(min(0.02, wait - 0.001))
        self.position = length
        self.rumble.all_off()
        time.sleep(0.03)
        self.running = False


class MidiParser:
    """MIDI byte stream -> MidiRumble (running status, real-time bytes in between)."""

    def __init__(self, rumble, channel=-1):
        self.rumble = rumble
        self.channel = channel          # -1: all
        self.status = 0
        self.need = 0
        self.have = 0
        self.d1 = 0
        self.messages = 0

    def feed(self, data):
        for b in data:
            self.byte(b)

    def byte(self, b):
        if b >= 0xF8:
            return                        # Real-time: ignore
        if b & 0x80:
            self.status = 0 if b >= 0xF0 else b
            self.need = 1 if (b & 0xF0) in (0xC0, 0xD0) else 2
            self.have = 0
            return
        if self.status == 0:
            return
        if self.have == 0 and self.need == 2:
            self.d1, self.have = b, 1
            return
        self.message(self.status, self.d1 if self.have == 1 else b, b if self.have == 1 else 0)
        self.have = 0

    def message(self, status, a, b):
        ch, kind = status & 0x0F, status & 0xF0
        if self.channel >= 0 and ch != self.channel:
            return
        self.messages += 1
        r = self.rumble
        if kind == 0x90:
            r.note_on(a, b)
        elif kind == 0x80:
            r.note_off(a)
        elif kind == 0xB0 and a == 64:
            r.set_sustain(b >= 64)
        elif kind == 0xB0 and a in (120, 123):
            r.all_off()


class MidiInput:
    """A MIDI device through mido (python-rtmidi backend)."""

    def __init__(self, rumble):
        self.rumble = rumble
        self.parser = MidiParser(rumble)
        self.port = None
        self.error = ""

    @staticmethod
    def available():
        try:
            import mido
            mido.get_input_names()
            return True
        except Exception:
            return False

    @staticmethod
    def devices():
        try:
            import mido
            return list(mido.get_input_names())
        except Exception:
            return []

    @property
    def listening(self):
        return self.port is not None

    @property
    def channel(self):
        return self.parser.channel

    @channel.setter
    def channel(self, value):
        self.parser.channel = value

    def listen(self, name):
        self.stop()
        self.error = ""
        try:
            import mido
            self.port = mido.open_input(name, callback=self._message)
        except Exception as e:          # No backend, no permission, device gone
            self.port = None
            self.error = "Can't open %s: %s" % (name, e)
            return False
        self.parser.messages = 0
        self.rumble.start()
        return True

    def _message(self, msg):
        if msg.type in ("clock", "active_sensing", "sysex"):
            return
        self.parser.feed(msg.bytes())

    def stop(self):
        if self.port is None:
            return
        try:
            self.port.close()
        except Exception:
            pass
        self.port = None
        self.rumble.stop()
