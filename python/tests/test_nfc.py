"""NFC scanning against the emulated Joy-Con (R): tag detection only, no tag contents."""
from conftest import make
from jctool import ops
from jctool.hidio import JOYCON_R

MIFARE_UID = bytes([0x04, 0x92, 0x79, 0x42, 0xFB, 0x16, 0x90])   # Seen on a Joy-Con with FW 4.19


class NfcUi:
    def __init__(self, ui):
        self.uid = self.tag = None
        ui.nfc_uid = lambda text: setattr(self, "uid", text)
        ui.nfc_tag = lambda text: setattr(self, "tag", text)


def test_mifare_tag_gives_its_uid_and_stops():
    fake, jc, ui = make(JOYCON_R)
    seen = NfcUi(ui)
    fake.nfc_tag = (0x04, MIFARE_UID)
    jc.enable_nfc_scanning = True
    assert jc.nfc_tag_info() == 0
    assert seen.uid == "UID:  04:92:79:42:FB:16:90\nType: MIFARE"
    assert "MIFARE" in seen.tag and "isn't supported" in seen.tag
    assert fake.nfc_reads == 0                       # No read attempts that can only fail
    assert fake.mcu_state == 0 and fake.input_mode == 0x3F   # MCU off, back to normal reports


def test_no_tag_is_error_7_with_help():
    fake, jc, ui = make(JOYCON_R)
    seen = NfcUi(ui)
    jc.enable_nfc_scanning = True
    res = jc.nfc_tag_info()
    assert res == 7 and ops.NFC_ERRORS[res] == "7NFCRECV"
    assert seen.tag == "No Tag detected!" and seen.uid is None
    assert "No tag detected" in ops.NFC_HELP[7]


def test_stopped_scan_ends_quietly():
    fake, jc, ui = make(JOYCON_R)
    seen = NfcUi(ui)
    polls = []
    original = fake._nfc_report

    def report(state):                     # The user presses Stop while it polls
        if state == 0x01:
            polls.append(1)
            if len(polls) == 3:
                jc.enable_nfc_scanning = False
        return original(state)
    fake._nfc_report = report
    jc.enable_nfc_scanning = True
    assert jc.nfc_tag_info() == 0
    assert len(polls) == 3 and fake.nfc_reads == 0 and seen.tag is None
    assert fake.mcu_state == 0 and fake.input_mode == 0x3F
