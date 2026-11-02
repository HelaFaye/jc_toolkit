"""The color dialog's eyedroppers: from the screen (jctool.screen_color: the system's picker,
or the pixel under the pointer, confirmed with a key) and from an image (any platform,
Android included: tap a pixel of a screenshot or a photo)."""
import threading

from kivy.clock import Clock
from kivy.core.image import Image as CoreImage
from kivy.core.window import Window
from kivy.metrics import dp
from kivy.uix.boxlayout import BoxLayout
from kivy.uix.image import Image
from kivy.uix.popup import Popup

from .. import screen_color
from .widgets import ACCENT, BACK, DARK, DIM, Btn, Filled, Lbl

KEY_ENTER, KEY_SPACE, KEY_ESC, KEY_NUMPAD_ENTER = 13, 32, 27, 271


def screen_available():
    """A screen picker exists on this system (not Android: apps can't see the screen)."""
    return screen_color.has_system_picker() or screen_color.has_pointer_picker()


class PointerSampler:
    """Follows the pointer over the whole screen: on_color((r, g, b), (x, y)) as it moves,
    then on_done(rgb) when Space or Enter takes the color, or on_done(None) on Esc. The
    window keeps the keyboard while the pointer is over other windows."""

    def __init__(self, on_color, on_done, rate=15):
        self.on_color, self.on_done = on_color, on_done
        self.rgb = None
        self.event = Clock.schedule_interval(self.sample, 1.0 / rate)
        Window.bind(on_keyboard=self.on_keyboard)

    def sample(self, *_):
        found = screen_color.under_pointer()
        if found:
            pos, self.rgb = found
            self.on_color(self.rgb, pos)

    def on_keyboard(self, window, key, *args):
        if key in (KEY_ENTER, KEY_SPACE, KEY_NUMPAD_ENTER):
            self.stop(self.rgb)
            return True
        if key == KEY_ESC:
            self.stop(None)
            return True                # Not the app's Esc (closing the popup or the app)
        return False

    def stop(self, result):
        self.event.cancel()
        Window.unbind(on_keyboard=self.on_keyboard)
        self.on_done(result)


def system_pick(start, on_done):
    """The system's own picker, off the UI thread: on_done(rgb or None) on the UI thread."""
    def work():
        rgb = screen_color.system_pick(start)
        Clock.schedule_once(lambda dt: on_done(rgb))
    threading.Thread(target=work, daemon=True).start()


def pixel_at(image, core, x, y):
    """The image's pixel under window point (x, y) (the widget fits the image, centered),
    as (r, g, b), or None outside it."""
    nw, nh = image.norm_image_size
    left, top = image.center_x - nw / 2, image.center_y + nh / 2
    if nw <= 0 or nh <= 0 or not (left <= x < left + nw and top - nh < y <= top):
        return None
    px = int((x - left) / nw * core.width)
    py = int((top - y) / nh * core.height)                  # read_pixel counts from the top row
    px, py = min(max(px, 0), core.width - 1), min(max(py, 0), core.height - 1)
    r, g, b = core.read_pixel(px, py)[:3]
    return tuple(int(round(v * 255)) for v in (r, g, b))


def pick_from_image(path, on_done):
    """A popup with the image: tapping it picks that pixel. on_done(rgb or None)."""
    try:
        core = CoreImage.load(path, keep_data=True)
    except Exception as e:                                  # Not an image Kivy can read
        on_done(None, "Can't open the image:\n%s" % e)
        return None
    box = Filled(orientation="vertical", color=BACK, padding=dp(8), spacing=dp(6))
    image = Image(texture=core.texture, fit_mode="contain")
    status = Lbl("Tap the color you want.", color=DIM, size_hint_y=None, height=dp(24))
    box.add_widget(image)
    box.add_widget(status)
    row = BoxLayout(size_hint_y=None, height=dp(34), spacing=dp(6))
    popup = Popup(title="Pick a color from the image", content=box, size_hint=(0.95, 0.95), background="",
                  background_color=DARK, title_color=ACCENT, separator_color=ACCENT)
    chosen = {"rgb": None}

    def touch(widget, t):
        if not widget.collide_point(*t.pos):
            return False
        rgb = pixel_at(widget, core, *t.pos)
        if rgb:
            chosen["rgb"] = rgb
            status.text = "#%02X%02X%02X  (Use it, or tap another color)" % rgb
            status.color = ACCENT
        return True
    image.bind(on_touch_down=touch, on_touch_move=touch)

    def use():
        popup.dismiss()
        on_done(chosen["rgb"], None)
    row.add_widget(Btn("Use", use, color=ACCENT))
    row.add_widget(Btn("Cancel", lambda: (popup.dismiss(), on_done(None, None))))
    box.add_widget(row)
    popup.open()
    return popup
