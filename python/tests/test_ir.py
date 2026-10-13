"""IR camera against the emulated Joy-Con (R), with the quirks seen on real hardware."""
import threading
import time

from conftest import make, wait_until
from jctool import ops
from jctool.hidio import JOYCON_R


def capture(jc, **settings):
    s = ops.IrSettings()
    for k, v in settings.items():
        setattr(s, k, v)
    return ops.ir_run(jc, s, False)


def test_capture_240x320_complete_frame_in_three_frames(right):
    fake, jc, ui = right
    assert capture(jc) == 0
    image, w, h = ui.frames[-1]
    assert (w, h) == (320, 240) and len(image) == 320 * 240
    assert jc.ir_last_frame_missing == 0 and fake.ir_frames_sent <= 3 * 256 + 16
    rgb, rw, rh = ops.ir_render(image, w, h, 2)
    assert (rw, rh) == (240, 320) and len(rgb) == 240 * 320 * 3


def test_quick_capture_two_frames_exposure_unchanged(right):
    fake, jc, ui = right
    jc.ir_quick_capture = True
    assert capture(jc, exposure=300) == 0
    assert fake.ir_frames_sent <= 2 * 256 + 16 and ui.exposures == []


def test_auto_exposure_at_60x80_doesnt_black_out(right):
    fake, jc, ui = right
    fake.ir_white_pixels = 8302
    assert capture(jc, resolution=2, exposure=160) == 0
    assert ui.frames[-1][1:] == (80, 60)
    assert jc.ir_exposure_value >= 60


def test_stale_and_stuck_camera_set_up_again(right):
    fake, jc, ui = right
    fake.ir_stale_captures = 1
    sets = fake.ir_mode_sets
    assert capture(jc) == 0 and fake.ir_mode_sets - sets == 2
    fake.ir_stuck_captures = 1
    sets = fake.ir_mode_sets
    assert capture(jc, resolution=2) == 0 and fake.ir_mode_sets - sets == 2
    fake.ir_stale_captures = 2
    assert capture(jc) == 10


def test_skipped_fragment_not_saved(right):
    fake, jc, ui = right
    fake.ir_skip_fragment = 253
    fake.ir_ignore_resend = True
    assert capture(jc) == 0
    assert fake.ir_skip_fragment == -1 and jc.ir_last_frame_missing == 0


def test_30x40_detects_320_pixel_rows(right):
    fake, jc, ui = right
    stuck = bytes((30 + i % 7) if (i % 320) < 160 else (200 - i % 5) for i in range(1200))
    real = bytes(((i % 40) * 5 + (i // 40) * 3 + (60 if (i % 40) % 10 == 0 else 0)) & 0xFF for i in range(1200))
    assert jc.ir_frame_has_other_width(stuck, 0x03)
    assert not jc.ir_frame_has_other_width(real, 0x03)


def run_stream(jc, settings, seconds, during=None):
    result = {}

    def worker():
        result["res"] = ops.ir_run(jc, settings, True)
    t = threading.Thread(target=worker)
    t.start()
    time.sleep(0.3)
    if during:
        during()
    time.sleep(seconds)
    jc.enable_ir_video = False
    t.join(10)
    return result.get("res")


def test_stream_live_config_and_stop(right):
    fake, jc, ui = right
    fake.ir_fragment_delay_ms = 1
    s = ops.IrSettings()
    writes = []

    def live():
        writes.append(fake.ir_register_writes)
        s.exposure = 200
        jc.ir_pending_live = s.config(jc, False, True)
    res = run_stream(jc, s, 0.8, live)
    assert res == 0 and fake.ir_register_writes > writes[0] and len(ui.frames) >= 2


def test_stream_restarts_when_camera_keeps_old_resolution(right):
    fake, jc, ui = right
    fake.ir_fragment_delay_ms = 1
    fake.ir_stuck_captures = 1
    sets = fake.ir_mode_sets
    s = ops.IrSettings()
    s.resolution = 3                      # 30x40, streamed without auto exposure
    assert run_stream(jc, s, 1.0) == 0
    assert fake.ir_mode_sets - sets == 2
