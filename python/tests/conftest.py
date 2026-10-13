import os
import sys
import time

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from jctool.core import JoyCon, Ui          # noqa: E402
from jctool.fake import FakeJoyCon          # noqa: E402
from jctool.hidio import JOYCON_L, JOYCON_R, PROCON   # noqa: E402


class RecordingUi(Ui):
    def __init__(self):
        self.frames = []
        self.status = []
        self.exposures = []
        self.custom = None
        self.button_reports = []
        self.on_frame = None

    def ir_frame(self, image, width, height):
        self.frames.append((image, width, height))
        if self.on_frame:
            self.on_frame(len(self.frames))

    def ir_status(self, text):
        self.status.append(text)

    def ir_exposure(self, exposure):
        self.exposures.append(exposure)

    def custom_command(self, sent, reply_cmd, reply):
        self.custom = (sent, reply_cmd, reply)

    def button_test(self, report, sensors, raw):
        self.button_reports.append((report, sensors))


def make(type_):
    fake = FakeJoyCon(type_)
    ui = RecordingUi()
    jc = JoyCon(fake.device(), ui)
    return fake, jc, ui


@pytest.fixture
def right():
    return make(JOYCON_R)


@pytest.fixture
def left():
    return make(JOYCON_L)


@pytest.fixture
def pro():
    return make(PROCON)


def wait_until(cond, seconds):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        if cond():
            return True
        time.sleep(0.005)
    return cond()
