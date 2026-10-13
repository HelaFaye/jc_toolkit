"""The command line (jctool-cli), driven through its standard input against the emulated
controller, like a person typing at the menu."""
import io
import os

import pytest

from jctool import cli


def run(capsys, monkeypatch, tmp_path, lines, args=("--demo",), before=None):
    """Runs the menu with these input lines (then exits); returns (output, fake)."""
    monkeypatch.chdir(tmp_path)
    app = cli.Cli(cli.parse_args(list(args)), stream=io.StringIO("\n".join(lines + ["0"]) + "\n"))
    if before:
        before(app)
    with pytest.raises(SystemExit) as e:
        app.run()
    assert e.value.code == 0
    return capsys.readouterr().out, app.fake


def reuse(fake):
    """A second run on the same emulated controller (the first run closed it at exit)."""
    def before(app):
        fake.closed = False
        app.fake = fake
    return before


def test_device_info_and_calibration(capsys, monkeypatch, tmp_path):
    out, fake = run(capsys, monkeypatch, tmp_path, ["2", "6"])
    assert "S/N:         XAW70012345678" in out and "Battery:     3.88V - 61%" in out
    assert "Calibration: factory" in out and "Right stick: center (2070, 2013)" in out


def test_colors_written_after_confirmation(capsys, monkeypatch, tmp_path):
    out, fake = run(capsys, monkeypatch, tmp_path, ["7", "12345", "zz3456", "123456", "abcdef", "y"])
    assert out.count("Enter a color as RRGGBB") == 2
    assert "[+] The colors were written to the device!" in out
    assert bytes(fake.spi[0x6050:0x6056]).hex() == "123456abcdef"


def test_colors_unchanged_when_not_confirmed(capsys, monkeypatch, tmp_path):
    out, fake = run(capsys, monkeypatch, tmp_path, ["7", "123456", "", "n"])
    assert bytes(fake.spi[0x6050:0x6053]).hex() == "ff3c28"


def test_backup_then_restore_colors(capsys, monkeypatch, tmp_path):
    out, fake = run(capsys, monkeypatch, tmp_path, ["8"])
    path = tmp_path / "spi_right_98B6E9123456.bin"
    assert "[+] Done dumping SPI!" in out and path.read_bytes() == bytes(fake.spi)
    data = bytearray(path.read_bytes())
    data[0x6050:0x6053] = b"\x01\x02\x03"
    path.write_bytes(bytes(data))

    out, _ = run(capsys, monkeypatch, tmp_path, ["9", str(path), "1", "y"], before=reuse(fake))
    assert "Backup of 98:b6:e9:12:34:56 loaded." in out and "[+] The colors were restored!" in out
    assert bytes(fake.spi[0x6050:0x6053]) == b"\x01\x02\x03"


def test_restore_refuses_a_partial_backup(capsys, monkeypatch, tmp_path):
    (tmp_path / "small.bin").write_bytes(b"\x00" * 100)
    out, _ = run(capsys, monkeypatch, tmp_path, ["9", "small.bin"])
    assert "[-] Partial backup!" in out


def test_serial_number_change(capsys, monkeypatch, tmp_path):
    out, fake = run(capsys, monkeypatch, tmp_path, ["10", "1", "XAW12345", "y", "y"])
    assert '"XAW12345"' in out and bytes(fake.spi[0x6000:0x6010]).lstrip(b"\x00") == b"XAW12345"


def test_manual_user_calibration_and_revert(capsys, monkeypatch, tmp_path):
    lines = ["11", "1", "y", "n", "800", "2000", "3200", "900", "2100", "3300", "n", "y", "6"]
    out, fake = run(capsys, monkeypatch, tmp_path, lines)
    assert "[+] The user calibration was written to SPI!" in out
    assert "Right stick: center (2000, 2100)  X [800 - 3200]  Y [900 - 3300]" in out
    out, _ = run(capsys, monkeypatch, tmp_path, ["11", "4", "y", "y"], before=reuse(fake))
    assert "Calibration in use: Factory" in out


def test_stick_device_parameters_for_pro(capsys, monkeypatch, tmp_path):
    out, fake = run(capsys, monkeypatch, tmp_path, ["11", "2", "0x99", "", "0x88", "", "y"], args=("--demo", "pro"))
    assert "[+] The Stick Device Parameters were written to SPI!" in out
    from jctool.core import decode_stick_params
    assert decode_stick_params(fake.spi[0x6089:0x608C])[0] == 0x99
    assert decode_stick_params(fake.spi[0x609B:0x609E])[0] == 0x88


def test_ir_capture_saves_png(capsys, monkeypatch, tmp_path):
    out, fake = run(capsys, monkeypatch, tmp_path, ["14", "1", "0"])
    assert "Status: Done! Saved to IRcamera.png" in out
    assert (tmp_path / "IRcamera.png").read_bytes()[:8] == b"\x89PNG\r\n\x1a\n"


def test_nfc_mifare_uid(capsys, monkeypatch, tmp_path):
    def tag(app):
        app.found()
        app.fake.nfc_tag = (0x04, bytes([0x04, 0x92, 0x79, 0x42, 0xFB, 0x16, 0x90]))
    out, _ = run(capsys, monkeypatch, tmp_path, ["15"], before=tag)
    assert "UID:  04:92:79:42:FB:16:90" in out and "MIFARE" in out and "Error" not in out


def test_debug_spi_subcommands_need_expert_mode(capsys, monkeypatch, tmp_path):
    cmd = ["16", "", "", "", "", "", "11", "0000F00010"]
    out, fake = run(capsys, monkeypatch, tmp_path, cmd)
    assert "are disabled!" in out and fake.spi_writes == 0
    out, fake = run(capsys, monkeypatch, tmp_path, ["16", "", "", "", "", "", "02", ""])
    assert "Sent:" in out


def test_tune_plays(capsys, monkeypatch, tmp_path):
    from jctool import tables
    monkeypatch.setattr(tables, "TUNE_SMO_OK", tables.TUNE_SMO_OK[:60])   # The first second
    out, fake = run(capsys, monkeypatch, tmp_path, ["13", "3"])
    assert "[+] The HD Rumble music has ended." in out and len(fake.rumbles) > 10


def test_midi_file(capsys, monkeypatch, tmp_path):
    from jctool.tests_data import small_midi
    (tmp_path / "song.mid").write_bytes(small_midi())
    out, fake = run(capsys, monkeypatch, tmp_path, ["13", "4", "song.mid", "0"])
    assert "Lead" in out and "[+] Done" in out


def test_list_hid_and_bad_choice(capsys, monkeypatch, tmp_path):
    import types
    from jctool import hidio
    # Not the system's HID devices: on macOS listing them waits for the Input Monitoring
    # permission, which a CI runner never answers
    fake_hid = types.ModuleType("fakehid")
    fake_hid.enumerate = lambda vid=0, pid=0: [{"path": b"/dev/fake0", "vendor_id": 0x057E, "product_id": 0x2007,
                                                "product_string": "Joy-Con (R)", "usage": 5, "usage_page": 1}]
    monkeypatch.setattr(hidio, "hid_module", lambda: fake_hid)
    out, _ = run(capsys, monkeypatch, tmp_path, ["17", "99", "abc"])
    assert "Backend: fakehid" in out and '0x2007 "Joy-Con (R)"' in out and out.count("[-] Invalid choice") == 2


def test_end_of_input_exits(capsys, monkeypatch, tmp_path):
    monkeypatch.chdir(tmp_path)
    app = cli.Cli(cli.parse_args(["--demo"]), stream=io.StringIO("7\n"))   # Ends at a prompt
    with pytest.raises(SystemExit) as e:
        app.run()
    assert e.value.code == 0


def test_guided_stick_calibration(capsys, monkeypatch, tmp_path):
    """Typed with pauses while the emulated stick rests, then turns along its edge."""
    import math
    import threading
    import time
    monkeypatch.chdir(tmp_path)
    r, w = os.pipe()
    app = cli.Cli(cli.parse_args(["--demo"]), stream=os.fdopen(r))
    app.found()
    fake = app.fake
    fake.stick_raw = (2000, 2100)
    out = os.fdopen(w, "w")

    def type_and_turn():
        for line in ("11", "1", "y", "y"):        # Set a right stick calibration, measured
            out.write(line + "\n")
            out.flush()
        time.sleep(1.5)                           # The center: the stick at rest
        a = 0.0
        end = time.monotonic() + 3
        while time.monotonic() < end:             # Rotating along the edge
            fake.stick_raw = (2000 + round(1200 * math.cos(a)), 2100 + round(1200 * math.sin(a)))
            a += math.radians(8)
            time.sleep(0.01)
        fake.stick_raw = (2000, 2100)
        for line in ("", "n", "y", "6", "0"):     # Enter: done; no 6-axis; write; read it back
            out.write(line + "\n")
            out.flush()
            time.sleep(0.3)
        out.close()
    threading.Thread(target=type_and_turn, daemon=True).start()
    with pytest.raises(SystemExit):
        app.run()
    text = capsys.readouterr().out
    import re
    m = re.search(r"\[\+\] Measured: X (\w+) / (\w+) / (\w+)   Y (\w+) / (\w+) / (\w+)", text)
    assert m, text[-3000:]
    measured = [int(v, 16) for v in m.groups()]
    # The edges depend on which points of the circle the reports sampled
    for got, want in zip(measured, [800, 2000, 3200, 900, 2100, 3300]):
        assert abs(got - want) <= 12, measured
    assert "[+] The user calibration was written to SPI!" in text
    assert "Right stick: center (2000, 2100)  X [%d - %d]  Y [%d - %d]" % (
        measured[0], measured[2], measured[3], measured[5]) in text
