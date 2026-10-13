"""Guided calibrations and MIDI on the HD Rumble."""
import math
import threading
import time

from conftest import make, wait_until
from jctool import calibration, midi
from jctool.hidio import JOYCON_R, PROCON
from jctool.tests_data import small_midi


def feed_stick(wizard, fake, jc, positions):
    """Reads a 0x30 report per position (like the app does) and feeds the wizard."""
    for pos in positions:
        fake.stick_raw = pos
        n, r = jc.read(49, 20)
        if n > 0 and r[0] == 0x30:
            wizard.add_report(r)


def test_stick_wizard_measures_and_saves(right):
    fake, jc, ui = right
    jc.set_input_report_mode(0x30)
    w = calibration.StickWizard(left=False)
    w.start()
    feed_stick(w, fake, jc, [(2000, 2100)] * 50)
    assert w.step == w.ROTATE and w.center == (2000, 2100)
    circle = [(2000 + round(1200 * math.cos(math.radians(a))), 2100 + round(1200 * math.sin(math.radians(a))))
              for a in range(0, 3 * 360 + 1, 5)]
    feed_stick(w, fake, jc, circle)
    assert w.can_finish and w.finish()
    assert w.result.as_list() == [800, 2000, 3200, 900, 2100, 3300]
    before_left = bytes(fake.spi[0x8010:0x801B])
    assert calibration.save_stick(jc, False, w.result) == 0
    assert bytes(fake.spi[0x8010:0x801B]) == before_left
    assert calibration.read_all(jc).user_right().as_list() == [800, 2000, 3200, 900, 2100, 3300]
    assert calibration.factory_stick(jc, False) == 0
    assert calibration.read_status(jc)[0] == "Factory"


def test_stick_wizard_waits_for_stillness():
    w = calibration.StickWizard(left=True)
    w.start()
    for i in range(100):
        w.add_sample(2000 + (i % 2) * 400, 2000)
    assert w.step == w.CENTER


def samples(values, n):
    return [list(values)] * n


def test_motion_wizard_one_position():
    w = calibration.MotionWizard()
    w.start()
    for s in samples([-45, -43, 4096 + 341, 25, -35, -36], 300):
        w.add_sample(s)
    assert w.result == [-45, -43, 341, 25, -35, -36]


def test_motion_wizard_restarts_when_moved():
    w = calibration.MotionWizard()
    w.start()
    for i in range(600):
        w.add_sample([60, -150, 4096 + 341, 900 if i % 2 else -900, 0, 0])
    assert w.result is None and "moved" in w.problem


def test_motion_wizard_two_positions_cancel_tilt_and_need_confirmation():
    w = calibration.MotionWizard(two_positions=True)
    w.start()
    for s in samples([-45 + 120, -43 - 80, 4096 + 341, 25, -35, -36], 300):
        w.add_sample(s)
    assert w.step == w.TURN
    for s in samples([-45, -43, 4096 + 341, 25, -35, -36 + 2000], 1600):
        w.add_sample(s)
        if abs(w.turned) >= 180:
            break
    assert w.can_confirm_turn
    for s in samples([-45 - 120, -43 + 80, 4096 + 341, 25, -35, -36], 400):
        w.add_sample(s)
    assert w.result is None and w.step == w.TURN         # Waits for the confirmation
    assert w.confirm_turn()
    for s in samples([-45 - 120, -43 + 80, 4096 + 341, 25, -35, -36], 300):
        w.add_sample(s)
    assert w.result == [-45, -43, 341, 25, -35, -36]


def test_motion_save_keeps_factory_sensitivities_and_factory_erases(right):
    fake, jc, ui = right
    assert calibration.save_motion(jc, [-45, -43, 341, 25, -35, -36]) == 0
    assert bytes(fake.spi[0x8026:0x8028]) == b"\xB2\xA1"
    assert bytes(fake.spi[0x802E:0x8034]) == bytes(fake.spi[0x6026:0x602C])
    assert bytes(fake.spi[0x803A:0x8040]) == bytes(fake.spi[0x6032:0x6038])
    assert calibration.read_status(jc)[0] == "User (motion)"
    assert calibration.factory_motion(jc) == 0
    assert bytes(fake.spi[0x8026:0x8040]) == b"\xFF" * 26


# ----------------------------------------------------------------------------------------
def test_midi_encoding():
    assert abs(midi.decode_high_hz(midi.encode(440, 0.8, 0, 0)) - 440) < 440 * 0.012
    assert midi.encode(0, 0, 0, 0) == midi.SILENCE
    assert midi.fold(midi.frequency(60), 400, 1252) == midi.frequency(72)
    assert midi.note_name(69) == "A4"


def test_midi_file_parsed():
    song = midi.MidiSong.load(small_midi())
    assert len(song.parts) == 2 and len(song.notes) == 5
    assert song.parts[0].name == "Lead" and song.parts[1].drums
    assert abs(song.length - 2.0) < 0.001          # 0.5 + 0.5 + 1.0 s (tempo halves at beat 2)


def rumble_for(jc):
    return midi.MidiRumble(jc.rumble_report, jc.enable_vibration)


def heard_hz(fake):
    out = []
    for rb in list(fake.rumbles):
        if rb == midi.SILENCE or rb == b"\x00\x00\x00\x00":
            continue
        hz = round(midi.decode_high_hz(rb))
        if hz not in out:
            out.append(hz)
    return out


def test_midi_file_plays_with_vibration_enabled_and_no_drums(right):
    fake, jc, ui = right
    player = midi.MidiFilePlayer(rumble_for(jc))
    player.play(midi.MidiSong.load(small_midi()))
    assert wait_until(lambda: not player.playing, 5)
    player.stop()
    heard = heard_hz(fake)
    has = lambda hz: any(abs(x - hz) <= hz * 0.012 for x in heard)
    assert has(523) and has(659) and not has(740), heard
    assert fake.rumbles[-1] == midi.SILENCE and not fake.vibration


def test_midi_input_sustain(right):
    fake, jc, ui = right
    r = rumble_for(jc)
    parser = midi.MidiParser(r)
    r.start()
    last = lambda: fake.rumbles[-1] if fake.rumbles else None
    parser.feed([0x90, 69, 100])
    assert wait_until(lambda: last() and last() != midi.SILENCE and abs(midi.decode_high_hz(last()) - 443) < 6, 1)
    parser.feed([0xB0, 64, 127, 0x80, 69, 0])
    time.sleep(0.1)
    assert last() != midi.SILENCE
    parser.feed([0xB0, 64, 0])
    assert wait_until(lambda: last() == midi.SILENCE, 1)
    r.stop()
    assert parser.messages == 4
