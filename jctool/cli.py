"""Joy-Con Toolkit command line: the interactive menu of the C++ jctool-cli (and the
window's features), on Windows, macOS and Linux.

    jctool-cli [-l] [-d] [-p l|r|pro] [--demo [l|r|pro]]

Prompts read from standard input, so the menu can also be driven by a script:
    printf '2\\n0\\n' | jctool-cli
Enter (an empty line) stops a long operation (backup, button test, IR stream, NFC scan,
HD Rumble).
"""
import argparse
import os
import queue
import sys
import threading
import time

from . import __version__, calibration, ops
from .core import JoyCon, Ui
from .hidio import (JOYCON_L, JOYCON_R, PROCON, TYPE_NAMES, Device, enumerate_controllers,
                    list_hid_devices)
from .png import write_png_rgb

PRIORITY = {"l": JOYCON_L, "r": JOYCON_R, "pro": PROCON}


class Input:
    """Standard input, read by a thread: prompts take lines from it, and long operations
    see a line typed meanwhile (on every platform, without select() on a console)."""

    def __init__(self, stream=None):
        self.lines = queue.Queue()
        self.pending = []          # Lines typed during a long operation, kept for the next prompts
        self.stream = stream or sys.stdin
        threading.Thread(target=self._read, name="stdin", daemon=True).start()

    def _read(self):
        for line in self.stream:
            self.lines.put(line.rstrip("\r\n"))
        self.lines.put(None)       # End of input

    def line(self, prompt=""):
        """A line, or None at the end of input."""
        if prompt:
            print(prompt, end="", flush=True)
        if self.pending:
            return self.pending.pop(0)
        return self.lines.get()

    def typed(self):
        """A line typed since the last check (None if nothing; "" for Enter alone)."""
        try:
            line = self.lines.get_nowait()
        except queue.Empty:
            return None
        if line is None:           # End of input doesn't stop it (a script ran out of lines); exits at the next prompt
            self.lines.put(None)
            self.ended = True
        return line

    ended = False                  # True once the end of input was seen


class CliUi(Ui):
    """What the window showed, printed (the C++ CLI's ui_* functions)."""

    def __init__(self, cli):
        self.cli = cli

    def poll(self):
        line = self.cli.input.typed()
        if line is None:
            return
        jc = self.cli.jc
        if line:
            if jc is not None and jc.enable_ir_video and line[:1] in ("e", "g", "r"):
                self.cli.ir_live_command = line
            else:
                self.cli.input.pending.append(line)
            return
        if jc is not None:
            jc.cancel_spi_dump = True
            jc.enable_button_test = False
            jc.enable_ir_video = False
            jc.enable_nfc_scanning = False
            jc.stop_playback = True
        print("\nStopping...")

    def spi_progress(self, offset):
        print("\r  %.2fKB of 512KB   (Enter to cancel)" % (offset / 1024.0), end="", flush=True)

    def custom_command(self, sent, reply_cmd, reply):
        print("\nSent:\n%s" % sent)
        if reply_cmd:
            print(reply_cmd)
        print("\n%s" % reply)

    def button_test_info(self, text):
        print(text[2:] if text.startswith("\n\n") else text)
        print("\nLive input (Enter to stop):\n\0337", end="")       # Save the cursor position

    def button_test(self, report, sensors, raw):
        print("\0338\033[J%s\n\n%s" % (report, sensors), end="", flush=True)

    def nfc_uid(self, text):
        print("\n" + text)

    def nfc_tag(self, text):
        print(text)

    def ntag_contents(self, data, pages):
        print(ops.ntag_text(data, pages))

    def ir_status(self, text):
        print("\r\033[K" + text, end="", flush=True)

    def ir_help(self, text):
        print("\r\033[K" + text.replace("\n", " "), end="", flush=True)

    def ir_exposure(self, exposure):
        self.cli.ir.exposure = exposure

    def ir_frame(self, image, width, height):
        self.cli.ir_frame(image, width, height)


class Cli:
    def __init__(self, args, stream=None):
        self.args = args
        self.input = Input(stream)
        self.ui = CliUi(self)
        self.jc = None
        self.fake = None
        self.accepted_third_party = None
        self.temp_celsius = True
        self.ir = ops.IrSettings()
        self.ir_preview = True
        self.ir_live_command = ""
        self.ir_frames_shown = 0
        self.ir_last = None
        self.vib = None

    # --- Input
    def read_line(self, prompt=""):
        line = self.input.line(prompt)
        if line is None:
            print()
            self.quit(0)
        return line

    def ask_yes_no(self, question):
        return self.read_line(question + " [y/N]: ").strip() in ("y", "Y", "yes", "Yes")

    def ask_int(self, question, current, lo, hi):
        """A number (decimal, or hex with 0x). Empty input keeps the current value."""
        while True:
            answer = self.read_line("%s [%d]: " % (question, current)).strip()
            if not answer:
                return current
            try:
                v = int(answer, 0)
            except ValueError:
                v = None
            if v is not None and lo <= v <= hi:
                return v
            print("  Enter a number from %d to %d." % (lo, hi))

    def ask_color(self, question, rgb):
        """(changed, rgb) for an RRGGBB answer; empty input keeps the color."""
        while True:
            answer = self.read_line("%s [%s]: " % (question, ops.hex_color(rgb)[1:])).strip()
            if not answer:
                return False, rgb
            try:
                return True, ops.parse_color(answer)
            except ValueError:
                print("  Enter a color as RRGGBB (hex), e.g. FF3C28.")

    # --- Connection
    def found(self):
        if self.args.demo:
            from .fake import FakeJoyCon
            if self.fake is None:
                self.fake = FakeJoyCon(PRIORITY.get(self.args.demo, JOYCON_R))
            return [self.fake.found()]
        found = enumerate_controllers()
        if self.args.p:
            preferred = [f for f in found if f.type == PRIORITY[self.args.p] and not f.third_party]
            found = preferred or found
        return found

    def connect(self, f):
        if f.third_party and f.path != self.accepted_third_party:
            print("\nA potential third-party device has been detected:\n\n\t%s\n" % f.name)
            if not self.ask_yes_no("Editing could be potentially unstable. Would you like to use this device anyways?"):
                return False
        self.close()
        try:
            dev = self.fake.device() if self.args.demo else Device.open(f)
        except Exception as e:
            print("[-] Failed to open %s: %s" % (f.path, e), file=sys.stderr)
            if sys.platform.startswith("linux"):
                print("    Check /dev/hidraw* permissions (udev rule) and that hid_nintendo isn't holding it.",
                      file=sys.stderr)
            return False
        if f.third_party:
            self.accepted_third_party = f.path
        if self.args.d:
            dev.enable_traffic_log("traffic_log.txt")
        self.jc = JoyCon(dev, self.ui)
        self.jc.silence_input_report()
        self.jc.set_led_busy()
        print("[+] Connected to %s" % TYPE_NAMES[self.jc.type])
        return True

    def select_device(self):
        found = self.found()
        if not found:
            print("[-] No Joy-Con or Pro Controller found\n"
                  "    Pair it first. On Linux, also check /dev/hidraw* permissions (udev rule)\n"
                  "    and that the hid_nintendo driver is not holding it.", file=sys.stderr)
            return False
        print("\n=== Available Devices ===")
        for i, f in enumerate(found):
            print("[%d] %s%s  %s" % (i, f.name, " (%s)" % f.serial if f.serial else "", f.path))
        choice = 0
        if len(found) > 1:
            answer = self.read_line("\nSelect device [0-%d]: " % (len(found) - 1)).strip()
            try:
                choice = int(answer)
            except ValueError:
                choice = -1
            if not 0 <= choice < len(found):
                print("[-] Invalid selection", file=sys.stderr)
                return False
        return self.connect(found[choice])

    def need_device(self):
        if self.jc is not None:
            return True
        print("[-] No device connected. Select one first.")
        return self.select_device()

    def close(self):
        if self.jc is not None:
            self.jc.dev.close()
            self.jc = None

    def quit(self, code):
        self.close()
        sys.exit(code)

    # --- Info, battery, LEDs, rumble
    def show_battery(self):
        volt, pct, state, _ = ops.battery(self.jc)
        print("Battery:     %.2fV - %d%%  (%s)" % (volt, pct, state))

    def show_temperature(self):
        t = ops.temperature_c(self.jc)
        print("Temperature: %.1f°C" % t if self.temp_celsius else "Temperature: %.1f°F" % (t * 1.8 + 32))

    def show_colors(self):
        c = ops.read_colors(self.jc)
        if c is None:
            print("Colors:      Error!")
            return
        print("Body:        %s" % ops.hex_color(c[0]))
        print("Buttons:     %s" % ops.hex_color(c[1]))
        if self.jc.type == PROCON:
            print("Grips:       left %s, right %s" % (ops.hex_color(c[2]), ops.hex_color(c[3])))

    def cmd_device_info(self):
        if not self.need_device():
            return
        jc = self.jc
        info = ops.device_info(jc)
        print("\nController:  %s" % TYPE_NAMES[jc.type])
        print("FW Version:  %s" % info.fw)
        print("MAC:         %s" % info.mac)
        print("S/N:         %s" % (jc.get_sn() if jc.type != PROCON else "Not supported"))
        self.show_battery()
        self.show_temperature()
        self.show_colors()
        text, _ = calibration.read_status(jc)
        print("Calibration: %s" % ("factory" if text == "Factory" else text[0].lower() + text[1:]))

    def cmd_battery(self):
        if not self.need_device():
            return
        print()
        self.show_battery()
        self.show_temperature()
        if self.ask_yes_no("Switch the temperature between Celsius and Fahrenheit?"):
            self.temp_celsius = not self.temp_celsius
            self.show_temperature()

    def cmd_player_led(self):
        if not self.need_device():
            return
        print("\nLED patterns (combine by adding):\n  0x01: Player 1\n  0x02: Player 2\n  0x04: Player 3\n"
              "  0x08: Player 4\n  Add 0x10-0x80 to flash the same LEDs instead")
        pattern = self.ask_int("Enter pattern (e.g. 0x01)", 0x01, 0, 0xFF)
        ok = self.jc.send_subcommand(0x30, bytes([pattern])) is not None
        print("[+] LED command acknowledged" if ok else "[-] No response")

    def cmd_vibration(self):
        if not self.need_device():
            return
        print("\n[*] Sending the rumble confirmation pattern...")
        self.jc.send_rumble()
        print("[+] Done")

    # --- Calibration
    def cmd_read_calibration(self):
        if not self.need_device():
            return
        print("\n" + calibration.read_all(self.jc).summary_text(), end="")

    def edit_stick(self, name, c):
        print("%s:" % name)
        c.x_minus = self.ask_int("  X minimum", c.x_minus, 0, 0xFFF)
        c.x_center = self.ask_int("  X center ", c.x_center, 0, 0xFFF)
        c.x_plus = self.ask_int("  X maximum", c.x_plus, 0, 0xFFF)
        c.y_minus = self.ask_int("  Y minimum", c.y_minus, 0, 0xFFF)
        c.y_center = self.ask_int("  Y center ", c.y_center, 0, 0xFFF)
        c.y_plus = self.ask_int("  Y maximum", c.y_plus, 0, 0xFFF)

    def _live_reports(self, step):
        """Reads 0x30 reports and calls step(report) until it returns True or a line is typed
        (returned: the line, or None when step finished)."""
        while True:
            n, r = self.jc.read(49, 20)
            if n > 12 and r[0] in (0x30, 0x21) and step(r):
                return None
            line = self.input.typed()
            if line is not None:
                return line
            if self.input.ended:       # No one left to press Enter: cancel
                return "q"

    def stick_wizard(self, left):
        """Guided stick calibration: a StickCal, or None if cancelled."""
        jc = self.jc
        w = calibration.StickWizard(left)
        w.start()
        jc.set_input_report_mode(0x30)
        last = [0.0, w.step]

        def show(r):
            w.add_report(r)
            if w.step != last[1]:
                last[1] = w.step
                if w.step == w.ROTATE:
                    print("\n2/2  Slowly rotate the stick along its outer edge, pushed all the way, until every\n"
                          "     direction is covered (2-3 turns). Then press Enter.")
            now = time.monotonic()
            if now - last[0] > 0.1:
                last[0] = now
                if w.step == w.CENTER:
                    print("\r  Now: X %03X  Y %03X  holding still %3d%%   " % (w.x, w.y, w.hold_progress * 100), end="", flush=True)
                else:
                    print("\r  Now: X %03X  Y %03X  X %03X-%03X  Y %03X-%03X  directions %d/%d   " % (
                        w.x, w.y, w.min_x, w.max_x, w.min_y, w.max_y, w.sectors_done(), w.SECTORS), end="", flush=True)
            return False
        result = None
        print("\n1/2  Let go of the stick so it rests at its center (q + Enter: cancel).")
        while True:
            line = self._live_reports(show)
            if line is None or line.startswith("q"):
                break
            if w.step == w.CENTER:
                print("\n  Still measuring the center: let go of the stick and wait, then press Enter.")
                continue
            if not w.can_finish:
                print("\n  Not every direction yet (%d/%d). Keep rotating, then press Enter." % (w.sectors_done(), w.SECTORS))
                continue
            if w.finish():
                result = w.result
                print("\n[+] Measured: X %03X / %03X / %03X   Y %03X / %03X / %03X (min / center / max)" % tuple(
                    [result.x_minus, result.x_center, result.x_plus, result.y_minus, result.y_center, result.y_plus]))
                break
            print("\n  " + w.problem)
        jc.set_input_report_mode(0x3F)
        if result is None:
            print("\nCalibration cancelled.")
        return result

    def motion_wizard(self, two_positions):
        """Guided 6-axis calibration: the origins, or None if cancelled."""
        jc = self.jc
        w = calibration.MotionWizard(two_positions=two_positions)
        w.start()
        jc.set_input_report_mode(0x30)
        jc.send_subcommand(0x40, b"\x01")
        last = [0.0, -1]

        def step(r):
            w.add_report(r)
            now = time.monotonic()
            if w.step != last[1]:
                last[1] = w.step
                if w.step == w.TURN:
                    print("\n2/3  Turn it 180 degrees on the surface (keep it flat), then press Enter.")
                elif w.step == w.MEASURE_B:
                    print("\n3/3  Keep it still...")
            if now - last[0] > 0.2:
                last[0] = now
                if w.step == w.TURN:
                    print("\r  Turned: %4.0f degrees   " % w.turned, end="", flush=True)
                elif w.step in (w.MEASURE_A, w.MEASURE_B):
                    print("\r  Measuring: %3d%%  %s   " % (w.progress * 100, w.problem), end="", flush=True)
            return w.result is not None
        print("\n1/%d  Put the controller on a flat surface and don't touch it (q + Enter: cancel)." %
              (3 if two_positions else 1))
        while True:
            line = self._live_reports(step)
            if line is None or (line and line.startswith("q")):
                break
            if w.step == w.TURN:
                if w.confirm_turn():
                    print("\n  Turn confirmed. Leave it still.")
                else:
                    print("\n  Not turned 180 degrees yet (%.0f)." % w.turned)
        jc.send_subcommand(0x40, b"\x00")
        jc.set_input_report_mode(0x3F)
        if w.result is None:
            print("\nCalibration cancelled.")
            return None
        print("\n[+] Measured offsets: acc %d %d %d, gyro %d %d %d" % tuple(w.result))
        return w.result

    def cmd_edit_calibration(self):
        if not self.need_device():
            return
        jc = self.jc
        print("\n 1. User stick and 6-axis calibration\n 2. Stick device parameters (factory)\n"
              " 3. Guided motion (6-axis) calibration\n 4. Revert to the factory calibration\n 0. Back")
        choice = self.ask_int("Select", 0, 0, 4)
        if choice == 1:
            info = calibration.read_all(jc)
            left, right = info.has_left, info.has_right
            lc = info.user_left() or info.factory_left()
            rc = info.user_right() or info.factory_right()
            l_on, r_on = info.user_left() is not None, info.user_right() is not None
            s_on = info.user_sensor_valid()
            origin = info.sensor_origin()
            print("\nA user calibration that is disabled is erased (the factory one is used).")
            for side, is_left, on, cal in (("left", True, l_on, lc), ("right", False, r_on, rc)):
                if not (left if is_left else right):
                    continue
                on = self.ask_yes_no("Keep a %s stick user calibration? (currently set)" % side if on else
                                     "Set a %s stick user calibration? (currently none)" % side)
                if on:
                    measured = None
                    if self.ask_yes_no("Measure it with the guided calibration? (n: type the values)"):
                        measured = self.stick_wizard(is_left)
                    if measured is not None:
                        cal = measured
                    else:
                        self.edit_stick("%s stick" % side.capitalize(), cal)
                if is_left:
                    l_on, lc = on, cal
                else:
                    r_on, rc = on, cal
            s_on = self.ask_yes_no("Keep a 6-axis user calibration? (currently set)" if s_on else
                                   "Set a 6-axis user calibration? (currently none)")
            if s_on:
                for i, axis in enumerate("XYZ"):
                    origin[i] = self.ask_int("  Acc %s origin (0-65535)" % axis, origin[i], 0, 0xFFFF)
                for i, axis in enumerate("XYZ"):
                    origin[3 + i] = self.ask_int("  Gyro %s origin (0-65535)" % axis, origin[3 + i], 0, 0xFFFF)
            if not self.ask_yes_no("Are you sure you want to continue?"):
                return
            res = calibration.write_user_calibration(jc, lc if left and l_on else None, rc if right and r_on else None,
                                                     origin if s_on else None)
            print("[+] The user calibration was written to SPI!" if res == 0 else
                  "[-] Failed to write user calibration to SPI! Please try again..")
        elif choice == 2:
            info = calibration.read_all(jc)
            p = list(info.stick_params())
            if jc.type == PROCON:
                p += list(info.stick_params(True))
            print("\nWarning! These are stick device parameters coming from factory.")
            first = "Left stick" if jc.type == PROCON else "Stick"
            p[0] = self.ask_int(first + " deadzone", p[0], 0, 0xFFF)
            p[1] = self.ask_int(first + " range ratio", p[1], 0, 0xFFF)
            if jc.type == PROCON:
                p[2] = self.ask_int("Right stick deadzone", p[2], 0, 0xFFF)
                p[3] = self.ask_int("Right stick range ratio", p[3], 0, 0xFFF)
            if not self.ask_yes_no("Are you sure you want to continue?"):
                return
            res = calibration.write_stick_params(jc, p[0:2], p[2:4] if jc.type == PROCON else None)
            print("[+] The Stick Device Parameters were written to SPI!" if res == 0 else
                  "[-] Failed to write the Stick Device Parameters to SPI! Please try again..")
        elif choice == 3:
            two = self.ask_yes_no("Measure in two positions (turned 180 degrees between them; cancels the surface's tilt)?")
            origins = self.motion_wizard(two)
            if origins is not None and self.ask_yes_no("Save it as the user 6-axis calibration?"):
                res = calibration.save_motion(jc, origins)
                print("[+] Saved!" if res == 0 else "[-] Failed to write it! Please try again..")
        elif choice == 4:
            print()
            res = 0
            if jc.type != JOYCON_R and self.ask_yes_no("Revert the %s to the factory calibration?" %
                                                       ("left stick" if jc.type == PROCON else "stick")):
                res |= calibration.factory_stick(jc, True)
            if jc.type != JOYCON_L and self.ask_yes_no("Revert the %s to the factory calibration?" %
                                                       ("right stick" if jc.type == PROCON else "stick")):
                res |= calibration.factory_stick(jc, False)
            if self.ask_yes_no("Revert the motion (6-axis) sensor to the factory calibration?"):
                res |= calibration.factory_motion(jc)
            print("[+] Done. Calibration in use: %s" % calibration.read_status(jc)[0] if res == 0 else
                  "[-] Failed to write! Please try again..")

    # --- Colors, backup, restore, S/N
    def cmd_colors(self):
        if not self.need_device():
            return
        jc = self.jc
        print()
        self.show_colors()
        c = ops.read_colors(jc)
        if c is None:
            return
        c = list(c)
        print("\nEnter new colors as RRGGBB (Enter keeps a color).")
        changed = False
        names = ["Body color", "Buttons color"] + (["Left grip color", "Right grip color"] if jc.type == PROCON else [])
        for i, name in enumerate(names):
            ch, c[i] = self.ask_color(name, c[i])
            changed |= ch
        if not changed:
            return
        jc.set_led_busy()
        print("\nDon't forget to make a backup first!")
        if not self.ask_yes_no("Are you sure you want to continue?"):
            return
        grips = (c[2], c[3]) if jc.type == PROCON else (None, None)
        res = ops.write_colors(jc, c[0], c[1], *grips)
        jc.send_rumble()
        if res == 0:
            print("[+] The colors were written to the device!")
            self.show_colors()
        else:
            print("[-] Failed to write the colors to the device!")

    def cmd_backup(self):
        if not self.need_device():
            return
        jc = self.jc
        name = ops.backup_filename(jc)
        if os.path.exists(name):
            print("The file %s already exists!" % name)
            if not self.ask_yes_no("Do you want to overwrite it?"):
                return
        print("\nDumping the SPI flash to %s (takes a few minutes)..." % name)
        jc.send_rumble()
        jc.set_led_busy()
        jc.cancel_spi_dump = False
        res = jc.dump_spi(name)
        print()
        if res == 0 and not jc.cancel_spi_dump:
            jc.send_rumble()
            print("[+] Done dumping SPI! Saved to %s" % name)
        elif jc.cancel_spi_dump:
            print("[-] Cancelled. %s is incomplete." % name)
        else:
            print("[-] Failed to dump the SPI chip!")
        jc.cancel_spi_dump = False

    def load_file(self, path):
        try:
            with open(path, "rb") as f:
                return f.read()
        except OSError as e:
            print("[-] Cannot open %s (%s)" % (path, e.strerror))
            return None

    def cmd_restore(self):
        if not self.need_device():
            return
        jc = self.jc
        path = self.read_line("\nSPI backup file (.bin): ").strip()
        data = self.load_file(path) if path else None
        if data is None:
            return
        backup = ops.Backup(data)
        err, same_mac = backup.check(jc.type, ops.device_info(jc).mac_bytes)
        if err:
            print("[-] " + err.replace("\n", "\n    "))
            return
        print("Backup of %s loaded." % backup.mac_text())
        allow_full = True
        if not same_mac:
            print('Different BT MAC address! The SPI backup is from another "%s".' % TYPE_NAMES[jc.type])
            if not self.ask_yes_no("Do you want to continue?"):
                return
            allow_full = False
        print("\n 1. Restore Color\n 2. Restore S/N\n 3. Restore user calibration\n 4. Factory reset user calibration")
        print(" 5. Full restore (factory configuration and user calibration)" if allow_full else
              " 5. Full restore: disabled, the backup does not match your device")
        print(" 0. Back")
        choice = self.ask_int("Select", 0, 0, 5)
        if choice == 0 or (choice == 5 and not allow_full):
            return
        l = r = s = False
        if choice in (3, 4):
            l = jc.type != JOYCON_R and self.ask_yes_no("  Left stick calibration?")
            r = jc.type != JOYCON_L and self.ask_yes_no("  Right stick calibration?")
            s = self.ask_yes_no("  6-axis (acc/gyro) calibration?")
        warnings = {1: "The device color will be restored with the backup values!",
                    2: "The serial number will be restored with the backup values!\n*Make sure that this backup was your original one!",
                    3: "The selected user calibration will be restored from the backup!",
                    4: "The selected user calibration will be factory resetted!",
                    5: "This will do a full restore of the Factory configuration and User calibration!"}
        print(warnings[choice])
        if not self.ask_yes_no("Are you sure you want to continue?"):
            return
        if choice == 1:
            res = ops.restore_colors(jc, backup)
            self.show_colors()
            done = "[+] The colors were restored!"
        elif choice == 2:
            res = ops.restore_sn(jc, backup)
            done = ('[+] The serial number was restored and changed to "%s"!' % jc.get_sn() if jc.type != PROCON
                    else "[+] The serial number was restored!")
        elif choice in (3, 4):
            res = ops.restore_user_calibration(jc, backup, l, r, s, factory_reset=choice == 4)
            done = "[+] The user calibration was restored!" if choice == 3 else "[+] The user calibration was factory resetted!"
        else:
            print("Restoring Factory Configuration and User Calibration... Don't disconnect your device!")
            res = ops.full_restore(jc, backup, lambda kb: print("\r  %.2fKB of 8KB" % kb, end="", flush=True))
            print()
            if res == 0:
                print("[+] The full restore was completed!\n    The controller was rebooted and it is now in pairing mode!\n"
                      "    Pair it with the Switch or PC again.")
                self.close()
                return
        print(done if res == 0 else "[-] Failed to restore or restore incomplete! Please try again..")

    def cmd_serial(self):
        if not self.need_device():
            return
        jc = self.jc
        if jc.type == PROCON:
            print("[-] Changing or restoring the S/N is not supported for Pro Controllers!")
            return
        print("\nCurrent S/N: %s\n\n 1. Change S/N\n 2. Restore S/N from the backup inside the controller\n 0. Back" % jc.get_sn())
        choice = self.ask_int("Select", 0, 0, 2)
        if choice == 1:
            while True:
                sn = self.read_line("New S/N (up to 15 ASCII characters): ")
                if ops.valid_sn(sn):
                    break
                print("  Use 1-15 non-extended ASCII characters.")
            print("This will change your Serial Number! Make a backup first!")
            if not self.ask_yes_no("Are you sure you want to continue?") or not self.ask_yes_no("Did you make a backup?"):
                return
            if ops.change_sn(jc, sn) == 0:
                print('[+] The S/N was written to the device! The new S/N is now "%s"!\n'
                      "    A backup of your S/N was created inside the SPI." % jc.get_sn())
            else:
                print("[-] Failed to write the S/N to the device!")
        elif choice == 2:
            if not self.ask_yes_no("Do you really want to restore it from the S/N backup inside your controller's SPI?"):
                return
            res = ops.restore_sn_from_controller(jc)
            if res == 2:
                print("[-] No S/N backup found inside your controller's SPI.\n"
                      "    This can happen if the first time you changed your S/N was with an older version\n"
                      "    of Joy-Con Toolkit. Otherwise, you never changed your S/N.")
            elif res == 0:
                print('[+] The S/N was restored to the device! The new S/N is now "%s"!' % jc.get_sn())
            else:
                print("[-] Failed to restore the S/N!")

    # --- Button test, HD Rumble
    def cmd_button_test(self):
        if not self.need_device():
            return
        print()
        self.jc.enable_button_test = True
        self.jc.button_test()
        self.jc.enable_button_test = False
        print()

    def cmd_hd_rumble(self):
        if not self.need_device():
            return
        jc = self.jc
        print('\n 1. Play an HD Rumble file (.bnvib, .jcvib)\n 2. Tune: Super Mario Bros.\n'
              ' 3. Tune: Super Mario Odyssey "OK"\n 4. Play a MIDI file on the HD Rumble\n 0. Back')
        choice = self.ask_int("Select", 0, 0, 4)
        if choice in (2, 3):
            print("Get the controller near your ear. If the tune is slow or choppy, get closer to the\n"
                  "Bluetooth adapter and keep line of sight. (Enter: stop)")
            jc.set_led_busy()
            jc.play_tune(0 if choice == 2 else 1)
            jc.send_rumble()
            print("[+] The HD Rumble music has ended.")
            return
        if choice == 4:
            return self.play_midi()
        if choice != 1:
            return
        path = self.read_line("File: ").strip()
        data = self.load_file(path) if path else None
        if data is None:
            return
        try:
            vib = ops.VibFile(data)
        except ValueError as e:
            print("[-] %s" % e)
            return
        print("Type: %s\nSample rate: %dms\nSamples: %d (%.2fs)" % (vib.type_name, vib.sample_rate, vib.samples, vib.seconds))
        if vib.type in (3, 4):
            vib.loop_times = self.ask_int("Loop times", 0, 0, 999)
        if vib.type >= 2:
            print("Equalizer (percent, 100 = unchanged):")
            eq = [self.ask_int("  %s (0-200)" % n, 100, 0, 200) // 10 for n in (
                "Low frequency amplitude ", "Low frequency pitch     ", "High frequency amplitude", "High frequency pitch    ")]
            vib.convert(*eq)
        print("Playing... (Enter to stop)")
        jc.play_hd_rumble(vib)
        print("[+] Done")

    def play_midi(self):
        from . import midi
        path = self.read_line("MIDI file: ").strip()
        data = self.load_file(path) if path else None
        if data is None:
            return
        try:
            song = midi.MidiSong.load(data)
        except ValueError as e:
            print("[-] %s" % e)
            return
        print("%d:%02d, %d notes, parts:" % (int(song.length) // 60, int(song.length) % 60, len(song.notes)))
        for i, p in enumerate(song.parts):
            print("  %d. %s" % (i + 1, p))
        part = self.ask_int("Part (0: all parts, no drums)", 0, 0, len(song.parts))
        player = midi.MidiFilePlayer(midi.MidiRumble(self.jc.rumble_report, self.jc.enable_vibration))
        player.play(song, part - 1 if part else -1)
        print("Playing... (Enter to stop)")
        while player.playing:
            if self.input.typed() is not None:
                break
            time.sleep(0.05)
        player.stop()
        print("[+] Done")

    # --- IR camera
    def ir_frame(self, image, width, height):
        rgb, w, h = ops.ir_render(image, width, height, self.ir.colorize)
        if not self.jc.enable_ir_video:
            write_png_rgb("IRcamera.png", rgb, w, h)
            self.ir_last = (rgb, w, h)
            return
        write_png_rgb("IRstream.png", rgb, w, h)
        print("\r\033[K", end="")
        self.ir_print_preview(rgb, w, h, self.ir_frames_shown > 0)
        self.ir_frames_shown += 1
        print("Streaming: IRstream.png updated. Enter: stop; e <us>: exposure; g <1-20>: gain; r <reg> <val>: register")
        if self.ir_live_command:
            self.ir_apply_live()

    def ir_print_preview(self, rgb, w, h, redraw):
        """24-bit color half blocks (each character is 2 pixels tall)."""
        if not self.ir_preview or not sys.stdout.isatty():
            return
        cols = 48
        rows = h * cols // w // 2
        out = ["\0338" if redraw else "\n\0337"]
        for r in range(rows):
            for c in range(cols):
                x = c * w // cols
                t = ((2 * r) * h // (2 * rows) * w + x) * 3
                b = ((2 * r + 1) * h // (2 * rows) * w + x) * 3
                out.append("\033[38;2;%d;%d;%dm\033[48;2;%d;%d;%dm▀" % (rgb[t], rgb[t + 1], rgb[t + 2], rgb[b], rgb[b + 1], rgb[b + 2]))
            out.append("\033[0m\n")
        print("".join(out), end="")

    def ir_apply_live(self):
        cmd, self.ir_live_command = self.ir_live_command.split(), ""
        try:
            if cmd[0] == "e" and len(cmd) == 2:
                self.ir.exposure = max(0, min(600, int(cmd[1], 0)))
            elif cmd[0] == "g" and len(cmd) == 2:
                self.ir.gain = max(1, min(20, int(cmd[1], 0)))
            elif cmd[0] == "r" and len(cmd) == 3:
                self.ir.custom_reg, self.ir.custom_val = int(cmd[1], 0) & 0xFFFF, int(cmd[2], 0) & 0xFF
            else:
                return
        except ValueError:
            return
        self.jc.ir_pending_live = self.ir.config(self.jc, False, True)
        print("Applied: exposure %dus, gain %d, register %04X = %02X" % (self.ir.exposure, self.ir.gain, self.ir.custom_reg, self.ir.custom_val))

    def ir_print_settings(self):
        ir, jc = self.ir, self.jc
        res = ops.IrSettings.RESOLUTIONS[ir.resolution]
        print("\n=== IR camera ===\n 1. Capture (saves IRcamera.png)\n 2. Stream (updates IRstream.png, Enter to stop)")
        print(" 3. Resolution:        %s%s" % (res, " (60x80 with auto exposure)" if ir.auto_exposure and ir.resolution == 3 else ""))
        print(" 4. Mode:              %s" % ops.IrSettings.MODES[ir.mode])
        print(" 5. Colorize:          %s" % ops.IrSettings.COLORS[ir.colorize])
        print(" 6. Far/Narrow leds:   %s, intensity %d/15" % ("on" if ir.leds_far else "off", ir.intensity_far))
        print(" 7. Near/Wide leds:    %s, intensity %d/16" % ("on" if ir.leds_near else "off", ir.intensity_near))
        print(" 8. Flashlight mode:   %s" % ("on" if ir.flashlight else "off"))
        print(" 9. Strobe flash mode: %s" % ("on" if ir.strobe else "off"))
        print("10. External IR filter: %s" % ("on" if ir.ex_filter else "off"))
        print("11. Selfie mode:       %s" % ("on" if ir.selfie else "off"))
        print("12. Exposure:          %dus" % ir.exposure)
        print("13. Auto exposure:     %s (streaming; Capture always uses it unless Quick capture)" % ("on" if ir.auto_exposure else "off"))
        print("14. Digital gain:      %d (streaming without auto exposure)" % ir.gain)
        print("15. De-noise:          %s, edge smoothing %d, color interpolation %d" % (
            "on" if ir.denoise else "off", ir.edge_smoothing, ir.color_interpolation))
        print("16. Quick capture:     %s" % ("on" if jc.ir_quick_capture else "off"))
        print("17. Terminal preview:  %s" % ("on" if self.ir_preview else "off"))
        print(" 0. Back")

    def ir_run(self, stream):
        jc = self.jc
        self.ir_frames_shown = 0
        self.ir_last = None
        self.ir_live_command = ""
        print("Status: Configuring%s" % ("" if stream else " (a capture takes up to ~30s at 240x320)"))
        res = ops.ir_run(jc, self.ir, stream)
        print()
        if res == 10:
            print("Status: Camera didn't apply the settings. Try again")
        elif res > 0:
            print("Status: Error %s!" % ops.IR_ERRORS.get(res, "?"))
        elif not stream:
            if self.ir_last is not None:
                self.ir_print_preview(*self.ir_last, redraw=False)
            print("Status: Done! Saved to IRcamera.png")
        else:
            print("Status: Standby")

    def cmd_ir_camera(self):
        if not self.need_device():
            return
        if self.jc.type != JOYCON_R:
            print("[-] The IR camera is only on the Joy-Con (R).")
            return
        ir = self.ir
        while True:
            self.ir_print_settings()
            c = self.ask_int("Select", 0, 0, 17)
            if c == 0:
                return
            elif c in (1, 2):
                self.ir_run(c == 2)
            elif c == 3:
                ir.resolution = self.ask_int("Resolution (0: 240x320, 1: 120x160, 2: 60x80, 3: 30x40)", ir.resolution, 0, 3)
            elif c == 4:
                ir.mode = self.ask_int("Mode (0: Capture, 1: Pointing, 2: Clustering)", ir.mode, 0, 2)
            elif c == 5:
                ir.colorize = self.ask_int("Colorize (0: Greyscale, 1: Night vision, 2: Ironbow, 3: Infrared)", ir.colorize, 0, 3)
            elif c == 6:
                ir.leds_far = self.ask_yes_no("Far/Narrow (75°) leds on?")
                if ir.leds_far:
                    ir.intensity_far = self.ask_int("Intensity (0-15)", ir.intensity_far, 0, 15)
            elif c == 7:
                ir.leds_near = self.ask_yes_no("Near/Wide (130°) leds on?")
                if ir.leds_near:
                    ir.intensity_near = self.ask_int("Intensity (0-16)", ir.intensity_near, 0, 16)
            elif c == 8:
                ir.flashlight = not ir.flashlight
            elif c == 9:
                ir.strobe = not ir.strobe
            elif c == 10:
                ir.ex_filter = not ir.ex_filter
            elif c == 11:
                ir.selfie = not ir.selfie
            elif c == 12:
                ir.exposure = self.ask_int("Exposure in us (0-600)", ir.exposure, 0, 600)
            elif c == 13:
                ir.auto_exposure = not ir.auto_exposure
            elif c == 14:
                ir.gain = self.ask_int("Digital gain (1-20, lossy)", ir.gain, 1, 20)
            elif c == 15:
                ir.denoise = self.ask_yes_no("De-noise on?")
                if ir.denoise:
                    ir.edge_smoothing = self.ask_int("Edge smoothing (0-255)", ir.edge_smoothing, 0, 255)
                    ir.color_interpolation = self.ask_int("Color interpolation (0-255)", ir.color_interpolation, 0, 255)
            elif c == 16:
                self.jc.ir_quick_capture = not self.jc.ir_quick_capture
                if self.jc.ir_quick_capture:
                    print("Quick capture: no auto exposure adjustment, uses the exposure as set, saves the 2nd frame.")
            elif c == 17:
                self.ir_preview = not self.ir_preview

    # --- NFC, debug, HID list, disconnect
    def cmd_nfc(self):
        if not self.need_device():
            return
        if self.jc.type == JOYCON_L:
            print("[-] NFC is only on the Joy-Con (R) and the Pro Controller.")
            return
        print("\nTouch the NFC area with an amiibo or NFC tag... (Enter to stop)")
        self.jc.enable_nfc_scanning = True
        res = self.jc.nfc_tag_info()
        self.jc.enable_nfc_scanning = False
        if res in ops.NFC_ERRORS:
            print("Error %s: %s" % (ops.NFC_ERRORS[res], ops.NFC_HELP.get(res, "Scan again.")))

    def cmd_debug(self):
        if not self.need_device():
            return
        print("\nAll values in hex. Empty input keeps the default.\n"
              "Command: 01 subcommand (with rumble data), 10 rumble only, 11 MCU/IR/NFC.")

        def hexval(prompt, default):
            v = self.read_line("%s [%02X]: " % (prompt, default)).strip()
            try:
                return int(v, 16) if v else default
            except ValueError:
                return default
        cmd = hexval("Command", 0x01)
        hf_freq, hf_amp = hexval("Rumble HF frequency", 0x00), hexval("Rumble HF amplitude", 0x01)
        lf_freq, lf_amp = hexval("Rumble LF frequency", 0x40), hexval("Rumble LF amplitude", 0x40)
        sub = hexval("Subcommand", 0x00)
        args = self.read_line("Arguments (hex bytes, e.g. 0102A0): ")
        if sub in ops.SPI_SUBCOMMANDS and not self.args.expert:
            print("[-] The subcommands 10 (SPI read), 11 (SPI write) and 12 (SPI sector erase) are disabled!\n"
                  "    Start with --expert to send them.")
            return
        self.jc.send_custom_command(ops.debug_command_args(cmd, hf_freq, hf_amp, lf_freq, lf_amp, sub, args))

    def cmd_disconnect(self):
        if self.jc is None:
            return
        if not self.ask_yes_no("Disconnect the controller (it turns off its Bluetooth connection)?"):
            return
        ops.disconnect(self.jc)
        self.close()

    MENU = [
        ("Detect and select device", "select_device"), ("Get device info", "cmd_device_info"),
        ("Get battery status", "cmd_battery"), ("Set player LED", "cmd_player_led"),
        ("Test vibration", "cmd_vibration"), ("Read calibration data", "cmd_read_calibration"),
        ("Colors (view / change)", "cmd_colors"), ("Backup SPI flash", "cmd_backup"),
        ("Restore from an SPI backup", "cmd_restore"), ("Serial number (change / restore)", "cmd_serial"),
        ("Edit calibration and stick parameters", "cmd_edit_calibration"),
        ("Button, stick and 6-axis test", "cmd_button_test"), ("HD Rumble player (files, tunes, MIDI)", "cmd_hd_rumble"),
        ("IR camera (Joy-Con R)", "cmd_ir_camera"), ("NFC / amiibo scan (Joy-Con R, Pro)", "cmd_nfc"),
        ("Debug: send a custom command", "cmd_debug"), ("List HID devices", "cmd_list_hid"),
        ("Disconnect the controller", "cmd_disconnect"),
    ]

    def cmd_list_hid(self):
        print(list_hid_devices())

    def print_menu(self):
        print("\n=== Joy-Con Toolkit (CLI) ===%s" % ("   [%s]" % TYPE_NAMES[self.jc.type] if self.jc else ""))
        for i, (name, _) in enumerate(self.MENU, 1):
            print("%2d. %s" % (i, name))
        print(" 0. Exit")

    def run(self):
        print("Joy-Con Toolkit v%s (CLI)\n%s\n" % (__version__, "=" * 30))
        if self.args.l:
            self.cmd_list_hid()
        if self.found():                       # Connect right away, like the window does
            self.select_device()
        while True:
            self.print_menu()
            line = self.input.line("Select: ")
            if line is None:
                line = "0"
            try:
                choice = int(line.strip())
            except ValueError:
                choice = -1
            if choice == 0:
                print("\n[*] Exiting...")
                self.quit(0)
            if 1 <= choice <= len(self.MENU):
                try:
                    getattr(self, self.MENU[choice - 1][1])()
                except (IOError, OSError) as e:
                    print("\n[-] The controller stopped answering (%s). Select it again (1)." % e)
                    self.close()
            else:
                print("[-] Invalid choice")


def parse_args(argv=None):
    p = argparse.ArgumentParser(prog="jctool-cli", description="Joy-Con Toolkit (command line)")
    p.add_argument("-l", action="store_true", help="list all HID devices first")
    p.add_argument("-d", action="store_true", help="log controller traffic to traffic_log.txt")
    p.add_argument("-p", choices=sorted(PRIORITY), help="prefer a controller type when several are connected")
    p.add_argument("--demo", nargs="?", const="r", choices=sorted(PRIORITY),
                   help="use an emulated controller (default: Joy-Con (R))")
    p.add_argument("--expert", action="store_true",
                   help="expert mode: allow the debug command's SPI subcommands (10 read, 11 write, 12 erase)")
    return p.parse_args(argv)


def main(argv=None):
    if os.name == "nt":
        os.system("")                          # Turns on ANSI escape sequences in the Windows console
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(errors="replace")
        except AttributeError:
            pass
    cli = Cli(parse_args(argv))
    try:
        cli.run()
    except KeyboardInterrupt:
        print()
        cli.quit(130)


if __name__ == "__main__":
    main()
