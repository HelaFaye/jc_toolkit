"""Device info, SPI, colors, backups, S/N, calibration editing, rumble, debug: against the
emulated controller (like the Mono self-test and the CLI tests)."""
import os
import struct
import threading

from conftest import make, wait_until
from jctool import calibration, ops
from jctool.core import JoyCon, mcu_crc8
from jctool.hidio import JOYCON_L, JOYCON_R, PROCON


def test_crc8_matches_table():
    assert mcu_crc8(b"") == 0
    assert mcu_crc8(bytes([0x01])) == 0x07


def test_device_info_battery_temperature(right):
    fake, jc, ui = right
    info = ops.device_info(jc)
    assert info.fw == "3.89" and info.mac == "98:B6:E9:12:34:56"
    assert jc.get_sn() == "XAW70012345678"
    volt, pct, state, _ = ops.battery(jc)
    assert (round(volt, 2), pct) == (3.88, 61)
    assert ops.temperature_c(jc) == 31.0
    assert ops.hex_color(ops.read_colors(jc)[0]) == "#FF3C28"


def test_padding_and_zero_length_read(right):
    fake, jc, ui = right
    jc.set_led_busy()
    n, buf = jc.read(0, 10)                 # Nothing queued: timeout
    assert n == 0 and len(buf) == 0x170


def test_colors_written(right, pro):
    fake, jc, ui = right
    assert ops.write_colors(jc, (0x12, 0x34, 0x56), (0xAB, 0xCD, 0xEF)) == 0
    assert bytes(fake.spi[0x6050:0x6056]).hex() == "123456abcdef"
    fake, jc, ui = pro
    assert ops.write_colors(jc, (0x32, 0x32, 0x32), (0xFF, 0xFF, 0xFF), (1, 2, 3), (4, 5, 6)) == 0
    assert bytes(fake.spi[0x6056:0x605C]).hex() == "010203040506"


def test_spi_dump_is_the_flash(tmp_path, right):
    fake, jc, ui = right
    path = tmp_path / ops.backup_filename(jc)
    assert path.name == "spi_right_98B6E9123456.bin"
    assert jc.dump_spi(str(path)) == 0
    assert path.read_bytes() == bytes(fake.spi)


def test_backup_checks_and_restores(right, left):
    fake, jc, ui = right
    backup = bytearray(fake.spi)
    mac = ops.device_info(jc).mac_bytes
    assert ops.Backup(backup[:1000]).check(JOYCON_R, mac)[0].startswith("Partial")
    assert ops.Backup(backup).check(JOYCON_L, mac)[0].startswith("Wrong backup")
    corrupt = bytearray(backup)
    corrupt[1] = 0
    assert ops.Backup(corrupt).check(JOYCON_R, mac)[0].startswith("Corrupt")
    other = bytearray(backup)
    other[0x15] = 0
    assert ops.Backup(other).check(JOYCON_R, mac) == (None, False)
    assert ops.Backup(backup).check(JOYCON_R, mac) == (None, True)

    colors = bytearray(backup)
    colors[0x6050:0x6053] = b"\x11\x22\x33"
    assert ops.restore_colors(jc, ops.Backup(colors)) == 0
    assert bytes(fake.spi[0x6050:0x6053]) == b"\x11\x22\x33"

    cal = bytearray(backup)
    cal[0x801B:0x8026] = bytes([0xB2, 0xA1, 1, 2, 3, 4, 5, 6, 7, 8, 9])
    assert ops.restore_user_calibration(jc, ops.Backup(cal), False, True, False) == 0
    assert bytes(fake.spi[0x801B:0x8026]).hex() == "b2a1010203040506070809"
    assert ops.restore_user_calibration(jc, None, False, True, False, factory_reset=True) == 0
    assert bytes(fake.spi[0x801B:0x8026]) == b"\xFF" * 11

    full = bytearray(backup)
    full[0x6005:0x600E] = b"NEWSERIAL"
    assert ops.full_restore(jc, ops.Backup(full)) == 0
    assert bytes(fake.spi[0x6005:0x600E]) == b"NEWSERIAL"


def test_serial_number(right):
    fake, jc, ui = right
    assert ops.valid_sn("TESTSN123") and not ops.valid_sn("") and not ops.valid_sn("x" * 16)
    assert ops.change_sn(jc, "TESTSN123") == 0
    assert bytes(fake.spi[0x6000:0x6010]).hex() == "0000000000000054455354534e313233"
    assert bytes(fake.spi[0xF000:0xF003]) == b"\x00\x00\x58"          # Original backed up
    assert jc.get_sn() == "TESTSN123"
    assert ops.restore_sn_from_controller(jc) == 0
    assert jc.get_sn() == "XAW70012345678"


def test_user_calibration_write_and_read_back(right):
    fake, jc, ui = right
    stick = calibration.StickCal(1000, 2000, 3000, 1100, 2100, 3100)
    assert calibration.write_user_calibration(jc, right=stick) == 0
    info = calibration.read_all(jc)
    assert info.user_right().as_list() == [1000, 2000, 3000, 1100, 2100, 3100]
    assert bytes(fake.spi[0x8026:0x8028]) == b"\xFF\xFF"             # Disabled 6-axis erased
    assert calibration.read_status(jc)[0] == "User (stick)"
    assert calibration.write_stick_params(jc, (150, 3000)) == 0
    assert bytes(fake.spi[0x6089:0x608C]).hex() == "9680bb"


def test_button_test_reports(right):
    fake, jc, ui = right
    jc.enable_button_test = True
    t = threading.Thread(target=jc.button_test)
    t.start()
    assert wait_until(lambda: len(ui.button_reports) >= 2, 3)
    jc.enable_button_test = False
    t.join(3)
    report, sensors = ui.button_reports[-1]
    assert "R Stick (Raw/Cal)" in report and "Acc/meter" in sensors
    assert fake.input_mode == 0x3F


def test_hd_rumble_files(right):
    fake, jc, ui = right
    raw = b"RRAW" + struct.pack(">HI", 1, 20) + bytes([0x00, 0x01, 0x40, 0x40]) * 20
    vib = ops.VibFile(raw)
    assert vib.type_name == "Raw HD Rumble" and vib.samples == 20
    writes = fake.writes
    jc.play_hd_rumble(vib)
    assert fake.writes - writes >= 20
    binary = struct.pack("<IHHI", 4, 3, 200, 80) + bytes([0x40, 0x60, 0x40, 0x80]) * 20
    vib = ops.VibFile(binary)
    assert vib.type_name == "Binary HD Rumble" and vib.sample_rate == 5
    vib.convert()
    looped = struct.pack("<IHHIII", 0xC, 3, 200, 5, 15, 80) + bytes([0x40, 0x60, 0x40, 0x80]) * 20
    vib = ops.VibFile(looped)
    assert vib.type_name == "Loop Binary HD Rumble" and (vib.loop_start, vib.loop_end) == (5, 15)


def test_tune_can_be_stopped(right):
    fake, jc, ui = right
    ui.poll = lambda: setattr(jc, "stop_playback", fake.writes > 20)
    jc.play_tune(0)
    assert 20 < fake.writes < 100


def test_debug_custom_command(right):
    fake, jc, ui = right
    args = ops.debug_command_args(subcmd=0x02, arguments="")
    jc.send_custom_command(args)
    sent, reply_cmd, reply = ui.custom
    assert "Cmd:  01   Subcmd: 02" in sent and reply.startswith("Subcmd Reply:") and "82 02 03 89" in reply
    assert ops.debug_command_args(arguments="123")[6:8] == bytes([0x01, 0x23])


def test_link_stats(right):
    fake, jc, ui = right
    jc.dev.take_link_stats()
    jc.get_battery()
    s = jc.dev.take_link_stats()
    assert s.writes >= 1 and s.reports >= 1 and s.errors == 0
