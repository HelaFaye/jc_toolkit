"""Draws the app icon: packaging/icon.png (512x512, also Android's), icon.ico (Windows),
icon.icns (macOS) and jctool/app/icon.png (the window's). Needs Pillow. Run from the python folder: python tools/make_icons.py"""
import os

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "packaging")


def draw(size=1024):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = size / 1024.0
    d.rounded_rectangle([40 * s, 40 * s, 984 * s, 984 * s], radius=200 * s, fill=(30, 33, 38, 255))
    # Joy-Con (L) neon blue and Joy-Con (R) neon red, with a gap between them
    d.rounded_rectangle([190 * s, 170 * s, 500 * s, 854 * s], radius=150 * s, fill=(10, 185, 230, 255))
    d.rectangle([380 * s, 170 * s, 500 * s, 854 * s], fill=(10, 185, 230, 255))
    d.rounded_rectangle([524 * s, 170 * s, 834 * s, 854 * s], radius=150 * s, fill=(255, 60, 40, 255))
    d.rectangle([524 * s, 170 * s, 644 * s, 854 * s], fill=(255, 60, 40, 255))
    dark = (30, 33, 38, 255)
    d.ellipse([285 * s, 300 * s, 425 * s, 440 * s], fill=dark)          # Left stick
    for cx, cy in ((355, 590), (300, 645), (410, 645), (355, 700)):     # D-pad buttons
        d.ellipse([(cx - 28) * s, (cy - 28) * s, (cx + 28) * s, (cy + 28) * s], fill=dark)
    for cx, cy in ((670, 320), (615, 375), (725, 375), (670, 430)):     # A B X Y
        d.ellipse([(cx - 28) * s, (cy - 28) * s, (cx + 28) * s, (cy + 28) * s], fill=dark)
    d.ellipse([600 * s, 520 * s, 740 * s, 660 * s], fill=dark)          # Right stick
    return img


def main():
    os.makedirs(OUT, exist_ok=True)
    img = draw()
    img.resize((512, 512), Image.LANCZOS).save(os.path.join(OUT, "icon.png"))
    img.resize((256, 256), Image.LANCZOS).save(os.path.join(HERE, "..", "jctool", "app", "icon.png"))   # Window icon
    img.save(os.path.join(OUT, "icon.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    img.save(os.path.join(OUT, "icon.icns"))
    print("Wrote the icons in", os.path.normpath(OUT), "and jctool/app")


if __name__ == "__main__":
    main()
