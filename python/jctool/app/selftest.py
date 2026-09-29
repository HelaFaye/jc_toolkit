"""The app's self-test (python -m jctool.app --selftest): drives the screens against the
emulated controller and checks what reaches it. Exits 0 when every check passed."""
import math
import os
import tempfile
import time

from kivy.clock import Clock

from .. import calibration, midi


class SelfTest:
    def __init__(self, app):
        self.app = app
        self.failed = 0
        self.steps = []
        self.tmp = tempfile.mkdtemp(prefix="jctool-selftest-")
        os.chdir(self.tmp)                      # IRcamera.png, traffic logs

    def check(self, ok, what):
        print(("PASS  " if ok else "FAIL  ") + what, flush=True)
        if not ok:
            self.failed += 1

    # A step: fn() returns None when done, or a (condition, seconds) to wait for first
    def start(self):
        self.steps = [self.connected, self.ir_capture, self.stick_wizard, self.motion_wizard, self.button_test,
                      self.backup, self.midi_file, self.tune, self.finish]
        Clock.schedule_once(lambda dt: self.next(), 1.0)

    def next(self):
        if not self.steps:
            return
        step = self.steps.pop(0)
        step()

    def wait(self, cond, seconds, then):
        end = time.monotonic() + seconds

        def tick(dt):
            if cond() or time.monotonic() > end:
                then()
                return False
            return True
        Clock.schedule_interval(tick, 0.05)

    @property
    def fake(self):
        return self.app.fake

    @property
    def jc(self):
        return self.app.jc

    # ------------------------------------------------------------------------------------
    def connected(self):
        app = self.app
        def done():
            self.check(app.lbl_sn.value.text == "XAW70012345678" and app.lbl_fw.value.text == "3.89"
                       and app.lbl_cal.value.text == "Factory" and "3.88V" in app.lbl_batt.text,
                       "connected: S/N, FW, calibration status, battery shown")
            self.next()
        self.wait(lambda: app.lbl_sn.value.text != "", 5, done)

    def ir_capture(self):
        app = self.app
        app.show("IR Camera")
        ir = app.panels["IR Camera"]
        ir.capture()
        def done():
            self.check(ir.image.texture is not None and ir.image.texture.size == (240, 320) and os.path.exists("IRcamera.png")
                       and "Done" in ir.status.text, "IR capture shown and saved as IRcamera.png (%s)" % ir.status.text)
            self.next()
        self.wait(lambda: not ir.btn_capture.disabled, 20, done)

    def stick_wizard(self):
        app = self.app
        app.show("Calibration")
        tab = app.panels["Calibration"].sticks
        self.fake.stick_raw = (2000, 2100)
        tab.start()
        angles = list(range(0, 3 * 360 + 1, 5))

        def rotate():
            self.check(tab.wizard.step == tab.wizard.ROTATE, "stick wizard: center taken")
            def move(dt):
                if not angles:
                    tab.finish()
                    r = tab.wizard.result
                    self.check(r is not None and r.as_list() == [800, 2000, 3200, 900, 2100, 3300],
                               "stick wizard: range measured (%s)" % (r.as_list() if r else None))
                    self.fake.stick_raw = None
                    if r is not None:
                        app.worker.run(lambda jc: calibration.save_stick(jc, False, r),
                                       lambda res: (app.refresh_calibration_status(), self.saved(res)))
                    else:
                        self.next()
                    return False
                a = math.radians(angles.pop(0))
                self.fake.stick_raw = (2000 + round(1200 * math.cos(a)), 2100 + round(1200 * math.sin(a)))
                return True
            Clock.schedule_interval(move, 0.02)
        self.wait(lambda: tab.wizard.step == tab.wizard.ROTATE, 5, rotate)

    def saved(self, res):
        app = self.app
        def done():
            self.check(res == 0 and app.lbl_cal.value.text == "User (stick)", "stick calibration saved, status: " + app.lbl_cal.value.text)
            self.next()
        self.wait(lambda: app.lbl_cal.value.text == "User (stick)", 3, done)

    def motion_wizard(self):
        app = self.app
        panel = app.panels["Calibration"]
        panel.select("Motion")
        tab = panel.motion
        tab.two.active = True
        self.fake.imu_raw = (-45 + 120, -43 - 80, 4096 + 341, 25, -35, -36)
        tab.start()
        w = tab.wizard

        def turn():
            self.fake.imu_raw = (-45, -43, 4096 + 341, 25, -35, -36 + 2000)
            self.wait(lambda: abs(w.turned) >= 180, 10, turned)

        def turned():
            self.fake.imu_raw = (-45 - 120, -43 + 80, 4096 + 341, 25, -35, -36)
            Clock.schedule_once(lambda dt: confirm(), 0.5)

        def confirm():
            self.check(not tab.btn_turned.disabled and w.step == w.TURN, "motion wizard: waits for Confirm turn")
            tab.confirm_turn()
            self.wait(lambda: w.result is not None, 10, measured)

        def measured():
            self.check(w.result == [-45, -43, 341, 25, -35, -36], "motion wizard (two positions): offsets %s" % w.result)
            self.fake.imu_raw = None
            self.next()
        self.wait(lambda: w.step == w.TURN, 10, turn)

    def button_test(self):
        app = self.app
        app.show("Button test")
        bt = app.panels["Button test"]
        bt.toggle()
        def stop():
            self.check("R Stick" in bt.report.text and "Acc/meter" in bt.sensors.text, "button test shows live input")
            bt.toggle()
            self.wait(lambda: bt.btn.text == "Turn on", 3, self.next)
        self.wait(lambda: "R Stick" in bt.report.text, 5, stop)

    def backup(self):
        app = self.app
        app.show("Backup")
        b = app.panels["Backup"]
        path = os.path.join(self.tmp, "backup.bin")
        b.dump(path)
        def done():
            ok = os.path.exists(path) and open(path, "rb").read() == bytes(self.fake.spi)
            self.check(ok, "SPI backup equals the flash")
            self.next()
        self.wait(lambda: b.btn_cancel.disabled, 60, done)

    def midi_file(self):
        app = self.app
        app.show("HD Rumble")
        panel = app.panels["HD Rumble"]
        panel.select("MIDI")
        tab = panel.midi
        from ..tests_data import small_midi
        tab.song = midi.MidiSong.load(small_midi())
        self.fake.rumbles.clear()
        tab.play_or_stop()
        def done():
            heard = [round(midi.decode_high_hz(r)) for r in self.fake.rumbles if r not in (midi.SILENCE, b"\x00\x00\x00\x00")]
            self.check(any(abs(h - 523) < 7 for h in heard) and any(abs(h - 659) < 8 for h in heard),
                       "MIDI file plays on the rumble (vibration enabled)")
            self.next()
        self.wait(lambda: len(self.fake.rumbles) > 3 and not tab.player.playing, 6, done)

    def tune(self):
        app = self.app
        panel = app.panels["HD Rumble"]
        panel.select("Files")
        writes = self.fake.writes
        panel.files.tune(0)
        def stop():
            panel.files.stop()
            self.wait(lambda: app.worker.busy_long is None, 3, lambda: (
                self.check(self.fake.writes - writes > 20, "tune plays and stops"), self.next()))
        self.wait(lambda: self.fake.writes - writes > 30, 5, stop)

    def finish(self):
        print("\nAll checks passed." if self.failed == 0 else "\n%d check(s) FAILED." % self.failed, flush=True)
        self.app.exit_code = 1 if self.failed else 0
        self.app.stop()
