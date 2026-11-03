"""BlueCubeMod (a GameCube controller as a Pro Controller): detection, its settings, and what
the toolkit enables for it, against the emulated one (FakeJoyCon(bluecubemod=True)). The
firmware itself is tested with the toolkit in its own repository (HelaFaye/BlueCubeMod)."""
import time

from conftest import RecordingUi
from test_cli import run
from jctool import bluecubemod, ops
from jctool.core import JoyCon
from jctool.fake import FakeJoyCon
from jctool.hidio import JOYCON_L, JOYCON_R, PROCON


def connect(**kw):
    fake = FakeJoyCon(PROCON, **kw) if kw else FakeJoyCon(PROCON)
    jc = JoyCon(fake.device(), RecordingUi())
    jc.bcm = bluecubemod.detect(jc)
    return fake, jc


def test_detected():
    fake, jc = connect(bluecubemod=True)
    info = jc.bcm
    assert info is not None and info.version == "3.0.0" and info.gc_connected
    assert bluecubemod.device_name(jc) == "Pro Controller (BlueCubeMod 3.0.0)"
    assert "GameCube controller connected" in info.describe()
    assert bluecubemod.sn_supported(jc) and jc.get_sn() == "BCM8A102030"
    assert not bluecubemod.has_feature(jc, "nfc") and not bluecubemod.has_feature(jc, "motion")


def test_other_controllers_quickly_not_detected():
    for t in (PROCON, JOYCON_L, JOYCON_R):
        fake = FakeJoyCon(t)
        jc = JoyCon(fake.device(), RecordingUi())
        start = time.monotonic()
        assert bluecubemod.detect(jc) is None
        assert time.monotonic() - start < 1.0
        assert bluecubemod.has_feature(jc, "nfc")
    assert not bluecubemod.sn_supported(jc) if jc.type == PROCON else True


def test_pro_controller_still_has_no_serial_number():
    fake, jc = connect()
    assert jc.bcm is None and not bluecubemod.sn_supported(jc)


def test_settings():
    fake, jc = connect(bluecubemod=True)
    assert bluecubemod.set_settings(jc, 2, 0x60, 1) == 0
    info = bluecubemod.detect(jc)
    assert (info.trigger_mode, info.threshold, info.stick_mode) == (2, 0x60, 1)
    assert "L / R" in info.settings_text() and "power-on" in info.settings_text()
    assert bluecubemod.set_settings(jc, 7, 0x60, 0) == 1


def test_serial_number_change_on_a_bluecubemod():
    fake, jc = connect(bluecubemod=True)
    assert ops.change_sn(jc, "GAMECUBE01") == 0
    assert jc.get_sn() == "GAMECUBE01"


def test_cli_device_info_serial_and_settings(capsys, monkeypatch, tmp_path):
    out, fake = run(capsys, monkeypatch, tmp_path,
                    ["2", "10", "1", "MYCUBE", "y", "y", "19", "y", "1", "150", "1", "15", "11", "3"],
                    args=("--demo", "bcm"))
    assert "[+] Connected to Pro Controller (BlueCubeMod 3.0.0)" in out
    assert "BlueCubeMod: BlueCubeMod 3.0.0, GameCube controller connected" in out
    assert "S/N:         BCM8A102030" in out
    assert 'The new S/N is now "MYCUBE"' in out
    assert "[+] Saved." in out and fake.bcm_settings == [1, 150, 1]
    assert "[-] A BlueCubeMod has no NFC reader." in out
    assert "[-] A BlueCubeMod has no motion sensors." in out


def test_cli_settings_on_another_controller(capsys, monkeypatch, tmp_path):
    out, fake = run(capsys, monkeypatch, tmp_path, ["19"], args=("--demo", "pro"))
    assert "[-] This controller isn't a BlueCubeMod" in out
